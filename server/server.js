import express from 'express';
import Database from 'better-sqlite3';
import crypto from 'node:crypto';
import { createRemoteJWKSet, jwtVerify } from 'jose';

const PORT = Number(process.env.PORT || 8080);
const DB_PATH = process.env.DB_PATH || './hvmc-pool.db';
const MICROSOFT_CLIENT_ID = process.env.MICROSOFT_CLIENT_ID || '';
const ADMIN_TOKEN = process.env.ADMIN_TOKEN || '';
const DEFAULT_LEASE_SECONDS = Number(process.env.LEASE_SECONDS || 3600);

if (!MICROSOFT_CLIENT_ID) throw new Error('MICROSOFT_CLIENT_ID is required');
if (!ADMIN_TOKEN) throw new Error('ADMIN_TOKEN is required');

const app = express();
app.use(express.json({ limit: '32kb' }));

const db = new Database(DB_PATH);
db.pragma('journal_mode = WAL');
db.exec(`
CREATE TABLE IF NOT EXISTS accounts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  slot TEXT NOT NULL UNIQUE,
  label TEXT NOT NULL,
  microsoft_oid TEXT UNIQUE,
  enabled INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS leases (
  id TEXT PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id),
  client_id TEXT NOT NULL,
  acquired_at TEXT NOT NULL,
  heartbeat_at TEXT NOT NULL,
  expires_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_leases_account ON leases(account_id);
CREATE INDEX IF NOT EXISTS idx_leases_expiry ON leases(expires_at);
`);

const insertSlot = db.prepare('INSERT OR IGNORE INTO accounts (slot,label,created_at) VALUES (?,?,?)');
for (let i = 1; i <= 5; i++) insertSlot.run(`account-${String(i).padStart(2, '0')}`, `HVMC Account ${i}`, new Date().toISOString());

const jwks = createRemoteJWKSet(new URL('https://login.microsoftonline.com/common/discovery/v2.0/keys'));

async function verifyMicrosoftIdToken(req) {
  const auth = String(req.headers.authorization || '');
  if (!auth.startsWith('Bearer ')) throw Object.assign(new Error('Missing bearer token'), { status: 401 });
  const token = auth.slice(7).trim();
  if (!token) throw Object.assign(new Error('Missing bearer token'), { status: 401 });

  const { payload } = await jwtVerify(token, jwks, {
    audience: MICROSOFT_CLIENT_ID,
    algorithms: ['RS256']
  });

  const oid = String(payload.oid || payload.sub || '');
  if (!oid) throw Object.assign(new Error('Token has no account identifier'), { status: 401 });
  return { oid, name: String(payload.name || ''), preferredUsername: String(payload.preferred_username || '') };
}

function cleanupExpired() {
  db.prepare('DELETE FROM leases WHERE expires_at <= ?').run(new Date().toISOString());
}

function isAdmin(req) {
  return String(req.headers['x-admin-token'] || '') === ADMIN_TOKEN;
}

app.get('/health', (_req, res) => res.json({ ok: true, service: 'hvmc-account-pool' }));

app.post('/v1/lease/acquire', async (req, res) => {
  try {
    cleanupExpired();
    const identity = await verifyMicrosoftIdToken(req);
    const clientId = String(req.body?.clientId || '').slice(0, 200);
    if (!clientId) return res.status(400).json({ error: 'clientId is required' });

    const mapped = db.prepare('SELECT * FROM accounts WHERE microsoft_oid = ? AND enabled = 1').get(identity.oid);
    let account = mapped;

    if (!account) {
      account = db.prepare(`SELECT * FROM accounts WHERE enabled = 1 AND microsoft_oid IS NULL ORDER BY id LIMIT 1`).get();
      if (!account) return res.status(409).json({ error: 'No unassigned HVMC accounts available' });
      db.prepare('UPDATE accounts SET microsoft_oid = ? WHERE id = ?').run(identity.oid, account.id);
      account = db.prepare('SELECT * FROM accounts WHERE id = ?').get(account.id);
    }

    const active = db.prepare('SELECT * FROM leases WHERE account_id = ? LIMIT 1').get(account.id);
    if (active) return res.status(409).json({ error: 'Account is already in use', accountName: account.label });

    const now = new Date();
    const expires = new Date(now.getTime() + DEFAULT_LEASE_SECONDS * 1000);
    const leaseId = crypto.randomUUID();
    db.prepare(`INSERT INTO leases (id,account_id,client_id,acquired_at,heartbeat_at,expires_at) VALUES (?,?,?,?,?,?)`)
      .run(leaseId, account.id, clientId, now.toISOString(), now.toISOString(), expires.toISOString());

    res.json({ leaseId, accountId: account.slot, accountName: account.label, expiresAt: expires.toISOString() });
  } catch (err) {
    res.status(err.status || 500).json({ error: err.message || 'internal error' });
  }
});

app.post('/v1/lease/heartbeat', async (req, res) => {
  try {
    cleanupExpired();
    const identity = await verifyMicrosoftIdToken(req);
    const leaseId = String(req.body?.leaseId || '');
    if (!leaseId) return res.status(400).json({ error: 'leaseId is required' });
    const lease = db.prepare(`SELECT leases.*, accounts.microsoft_oid FROM leases JOIN accounts ON accounts.id = leases.account_id WHERE leases.id = ?`).get(leaseId);
    if (!lease || lease.microsoft_oid !== identity.oid) return res.status(404).json({ error: 'lease not found' });
    const expires = new Date(Date.now() + DEFAULT_LEASE_SECONDS * 1000).toISOString();
    db.prepare('UPDATE leases SET heartbeat_at = ?, expires_at = ? WHERE id = ?').run(new Date().toISOString(), expires, leaseId);
    res.json({ ok: true, expiresAt: expires });
  } catch (err) {
    res.status(err.status || 500).json({ error: err.message || 'internal error' });
  }
});

app.post('/v1/lease/release', async (req, res) => {
  try {
    const identity = await verifyMicrosoftIdToken(req);
    const leaseId = String(req.body?.leaseId || '');
    const lease = db.prepare(`SELECT leases.*, accounts.microsoft_oid FROM leases JOIN accounts ON accounts.id = leases.account_id WHERE leases.id = ?`).get(leaseId);
    if (!lease || lease.microsoft_oid !== identity.oid) return res.status(404).json({ error: 'lease not found' });
    db.prepare('DELETE FROM leases WHERE id = ?').run(leaseId);
    res.json({ ok: true });
  } catch (err) {
    res.status(err.status || 500).json({ error: err.message || 'internal error' });
  }
});

app.get('/v1/status', (req, res) => {
  if (!isAdmin(req)) return res.status(403).json({ error: 'forbidden' });
  cleanupExpired();
  const rows = db.prepare(`SELECT accounts.slot,accounts.label,accounts.enabled,CASE WHEN leases.id IS NULL THEN 0 ELSE 1 END AS busy,leases.client_id,leases.expires_at FROM accounts LEFT JOIN leases ON leases.account_id = accounts.id ORDER BY accounts.id`).all();
  res.json({ accounts: rows });
});

app.post('/v1/admin/reset-mapping', (req, res) => {
  if (!isAdmin(req)) return res.status(403).json({ error: 'forbidden' });
  db.prepare('DELETE FROM leases').run();
  db.prepare('UPDATE accounts SET microsoft_oid = NULL').run();
  res.json({ ok: true });
});

app.listen(PORT, '127.0.0.1', () => console.log(`HVMC account pool listening on 127.0.0.1:${PORT}`));
