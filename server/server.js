import express from 'express';
import Database from 'better-sqlite3';
import crypto from 'node:crypto';
import path from 'node:path';
import nodemailer from 'nodemailer';
import { fileURLToPath } from 'node:url';
import { jwtVerify, createRemoteJWKSet } from 'jose';
import { registerPcManagement } from './pc-management.js';

const app = express();
app.disable('x-powered-by');
app.set('trust proxy', 1);
app.use(express.json({ limit: '32kb' }));

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
app.use(express.static(path.join(__dirname, 'public'), { index: false, dotfiles: 'deny' }));
app.get('/', (_req, res) => res.redirect('/admin'));
app.get('/admin', (_req, res) => res.sendFile(path.join(__dirname, 'public', 'admin.html')));
app.get('/admin/', (_req, res) => res.redirect('/admin'));
app.get('/admin/pcs', (_req, res) => res.sendFile(path.join(__dirname, 'public', 'pc-admin.html')));
app.get('/admin/pcs/', (_req, res) => res.redirect('/admin/pcs'));

const PORT = Number(process.env.PORT || 8080);
const DB_PATH = process.env.DB_PATH || './hvmc-pool.db';
const MICROSOFT_CLIENT_ID = String(process.env.MICROSOFT_CLIENT_ID || '');
const ADMIN_TOKEN = String(process.env.ADMIN_TOKEN || '');
const ADMIN_USERNAME = String(process.env.ADMIN_USERNAME || 'bendemen');
const ADMIN_PASSWORD_HASH = String(process.env.ADMIN_PASSWORD_HASH || '');
const DEFAULT_LEASE_SECONDS = Number(process.env.LEASE_SECONDS || 3600);
const MS_AUTHORITY = 'https://login.microsoftonline.com/consumers';
const MS_SCOPE = 'openid profile offline_access XboxLive.signin';
const POOL_ENCRYPTION_KEY_B64 = String(process.env.POOL_ENCRYPTION_KEY || '');
const EMAIL_FROM = String(process.env.EMAIL_FROM || 'automail@hvmc.nl');
const EMAIL_SMTP_HOST = String(process.env.EMAIL_SMTP_HOST || '');
const EMAIL_SMTP_PORT = Number(process.env.EMAIL_SMTP_PORT || 587);
const EMAIL_SMTP_SECURE = /^(1|true|yes)$/i.test(String(process.env.EMAIL_SMTP_SECURE || 'false'));
const EMAIL_SMTP_USER = String(process.env.EMAIL_SMTP_USER || '');
const EMAIL_SMTP_PASSWORD = String(process.env.EMAIL_SMTP_PASSWORD || '');
const EMAIL_AUTH_TTL_MS = 10 * 60 * 1000;
const EMAIL_AUTH_COOLDOWN_MS = 60 * 1000;
const EMAIL_AUTH_MAX_ATTEMPTS = 5;
const EMAIL_AUTH_ALLOWED = new Set(['info@bendemen.nl', 'bendemenbv@gmail.com']);

if (!MICROSOFT_CLIENT_ID) throw new Error('MICROSOFT_CLIENT_ID is required');
if (!ADMIN_PASSWORD_HASH) throw new Error('ADMIN_PASSWORD_HASH is required');
if (!POOL_ENCRYPTION_KEY_B64) throw new Error('POOL_ENCRYPTION_KEY is required');

let poolKey;
try {
  poolKey = Buffer.from(POOL_ENCRYPTION_KEY_B64, 'base64');
  if (poolKey.length !== 32) throw new Error('must decode to exactly 32 bytes');
} catch (err) {
  throw new Error(`POOL_ENCRYPTION_KEY invalid: ${err.message}`);
}

let emailTransporter;
function getEmailTransporter() {
  if (!EMAIL_SMTP_HOST) throw new Error('Email authentication is not configured on the server (EMAIL_SMTP_HOST missing).');
  if (!emailTransporter) {
    emailTransporter = nodemailer.createTransport({
      host: EMAIL_SMTP_HOST,
      port: EMAIL_SMTP_PORT,
      secure: EMAIL_SMTP_SECURE,
      auth: EMAIL_SMTP_USER ? { user: EMAIL_SMTP_USER, pass: EMAIL_SMTP_PASSWORD } : undefined
    });
  }
  return emailTransporter;
}
async function sendAdminEmailCode(to, code) {
  await getEmailTransporter().sendMail({
    from: EMAIL_FROM,
    to,
    subject: 'HVMC Account Dashboard',
    text: `Je HVMC Account Dashboard-verificatiecode is: ${code}\n\nDeze code is 10 minuten geldig. Als je dit niet hebt aangevraagd, kun je deze e-mail negeren.`,
    html: `<div style="font-family:Arial,sans-serif;max-width:520px"><h2>HVMC Account Dashboard</h2><p>Je inlogcode is:</p><p style="font-size:32px;font-weight:800;letter-spacing:8px">${code}</p><p>Deze code is 10 minuten geldig.</p><p>Heb je dit niet aangevraagd? Dan kun je deze e-mail negeren.</p></div>`
  });
}

const db = new Database(DB_PATH);
db.pragma('journal_mode = WAL');
db.pragma('foreign_keys = ON');
db.exec(`
CREATE TABLE IF NOT EXISTS accounts (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  slot TEXT NOT NULL UNIQUE,
  label TEXT NOT NULL,
  microsoft_oid TEXT UNIQUE,
  microsoft_username TEXT,
  microsoft_refresh_token_enc TEXT,
  enabled INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS leases (
  id TEXT PRIMARY KEY,
  account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  client_id TEXT NOT NULL,
  acquired_at TEXT NOT NULL,
  heartbeat_at TEXT NOT NULL,
  expires_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_leases_account ON leases(account_id);
CREATE INDEX IF NOT EXISTS idx_leases_expiry ON leases(expires_at);
CREATE TABLE IF NOT EXISTS pool_meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
`);
try { db.exec('ALTER TABLE accounts ADD COLUMN microsoft_username TEXT'); } catch {}
try { db.exec('ALTER TABLE accounts ADD COLUMN microsoft_refresh_token_enc TEXT'); } catch {}

const initialized = db.prepare("SELECT value FROM pool_meta WHERE key = 'initialized'").get();
if (!initialized) {
  const existingCount = Number(db.prepare('SELECT COUNT(*) AS count FROM accounts').get().count);
  if (existingCount === 0) {
    const insertSlot = db.prepare('INSERT OR IGNORE INTO accounts (slot,label,created_at) VALUES (?,?,?)');
    for (let i = 1; i <= 5; i++) {
      insertSlot.run(`account-${String(i).padStart(2, '0')}`, `HVMC Account ${i}`, new Date().toISOString());
    }
  }
  db.prepare("INSERT OR REPLACE INTO pool_meta (key,value) VALUES ('initialized','1')").run();
}

const sessions = new Map();
const loginAttempts = new Map();
const emailAuthCodes = new Map();
const emailAuthChallenges = new Map();
const emailAuthSendState = new Map();
const linkAttempts = new Map();
const launcherRate = new Map();
const SESSION_TTL_MS = 12 * 60 * 60 * 1000;
const LOGIN_WINDOW_MS = 10 * 60 * 1000;
const MAX_LOGIN_ATTEMPTS = 10;
const LAUNCHER_WINDOW_MS = 60 * 1000;
const MAX_LAUNCHER_ATTEMPTS = 30;

function cleanupExpired() {
  const now = new Date().toISOString();
  db.prepare('DELETE FROM leases WHERE expires_at <= ?').run(now);
  for (const [ip, info] of launcherRate) if (info.resetAt <= Date.now()) launcherRate.delete(ip);
}
function cleanupState() {
  const now = Date.now();
  for (const [token, session] of sessions) if (session.expiresAt <= now) sessions.delete(token);
  for (const [ip, info] of loginAttempts) if (info.resetAt <= now) loginAttempts.delete(ip);
  for (const [challengeId, info] of emailAuthCodes) if (info.expiresAt <= now) emailAuthCodes.delete(challengeId);
  for (const [challengeId, info] of emailAuthChallenges) if (info.expiresAt <= now) emailAuthChallenges.delete(challengeId);
  for (const [key, info] of emailAuthSendState) if (info.resetAt <= now) emailAuthSendState.delete(key);
  for (const [id, attempt] of linkAttempts) if (attempt.expiresAt <= now) linkAttempts.delete(id);
  cleanupExpired();
}
setInterval(cleanupState, 60_000).unref();

function safeEqual(a, b) {
  const aa = Buffer.from(String(a));
  const bb = Buffer.from(String(b));
  return aa.length === bb.length && crypto.timingSafeEqual(aa, bb);
}
function verifyPassword(password) {
  const parts = ADMIN_PASSWORD_HASH.split('$');
  if (parts.length !== 6 || parts[0] !== 'scrypt') return false;
  const [, nText, rText, pText, saltB64, hashB64] = parts;
  const N = Number(nText), r = Number(rText), p = Number(pText);
  if (!Number.isInteger(N) || !Number.isInteger(r) || !Number.isInteger(p)) return false;
  try {
    const salt = Buffer.from(saltB64, 'base64');
    const expected = Buffer.from(hashB64, 'base64');
    const actual = crypto.scryptSync(password, salt, expected.length, {
      N, r, p, maxmem: Math.max(64 * 1024 * 1024, 128 * N * r + 1024)
    });
    return safeEqual(actual, expected);
  } catch { return false; }
}
function getSessionToken(req) {
  return String(req.headers.authorization || '').replace(/^Bearer\\s+/i, '').trim();
}
function isAdmin(req) {
  const legacy = String(req.headers['x-admin-token'] || '');
  if (ADMIN_TOKEN && safeEqual(legacy, ADMIN_TOKEN)) return true;
  const token = getSessionToken(req);
  const session = sessions.get(token);
  if (!session || session.expiresAt <= Date.now()) return false;
  return session.role === 'admin';
}
function requireAdmin(req, res) {
  if (!isAdmin(req)) {
    res.status(401).json({ error: 'unauthorized' });
    return false;
  }
  return true;
}
function createAdminSession(res) {
  const token = crypto.randomBytes(32).toString('base64url');
  const expiresAt = Date.now() + SESSION_TTL_MS;
  sessions.set(token, { role: 'admin', username: ADMIN_USERNAME, expiresAt });
  res.json({ ok: true, token, expiresAt: new Date(expiresAt).toISOString() });
}
function normalizeEmail(value) {
  return String(value || '').trim().toLowerCase();
}
function hashAuthCode(code) {
  return crypto.createHash('sha256').update(String(code)).digest('hex');
}
function emailRateKey(req, email) {
  return `${String(req.ip || req.socket.remoteAddress || 'unknown')}|${email}`;
}

registerPcManagement(app, db, requireAdmin);

async function readJsonResponse(response, context) {
  const raw = await response.text();
  if (!raw.trim()) {
    throw new Error(`${context} returned an empty response (HTTP ${response.status}).`);
  }
  try {
    return JSON.parse(raw);
  } catch {
    const preview = raw.length > 400 ? `${raw.slice(0, 400)}...` : raw;
    throw new Error(`${context} returned invalid JSON (HTTP ${response.status}): ${preview}`);
  }
}
function encryptSecret(value) {
  const iv = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv('aes-256-gcm', poolKey, iv);
  const ciphertext = Buffer.concat([cipher.update(String(value), 'utf8'), cipher.final()]);
  const tag = cipher.getAuthTag();
  return [iv, tag, ciphertext].map(x => x.toString('base64url')).join('.');
}
function decryptSecret(payload) {
  const [ivText, tagText, dataText] = String(payload || '').split('.');
  if (!ivText || !tagText || !dataText) throw new Error('Invalid encrypted credential');
  const decipher = crypto.createDecipheriv('aes-256-gcm', poolKey, Buffer.from(ivText, 'base64url'));
  decipher.setAuthTag(Buffer.from(tagText, 'base64url'));
  return Buffer.concat([decipher.update(Buffer.from(dataText, 'base64url')), decipher.final()]).toString('utf8');
}
async function verifyMicrosoftIdToken(req) {
  const auth = String(req.headers.authorization || '');
  if (!auth.startsWith('Bearer ')) throw Object.assign(new Error('Missing bearer token'), { status: 401 });
  const token = auth.slice(7).trim();
  const jwks = createRemoteJWKSet(new URL(`${MS_AUTHORITY}/discovery/v2.0/keys`));
  const { payload } = await jwtVerify(token, jwks, { audience: MICROSOFT_CLIENT_ID, algorithms: ['RS256'] });
  const oid = String(payload.oid || payload.sub || '');
  if (!oid) throw Object.assign(new Error('Token has no account identifier'), { status: 401 });
  return { oid, name: String(payload.name || ''), preferredUsername: String(payload.preferred_username || payload.email || '') };
}
async function microsoftDeviceStart() {
  const response = await fetch(`${MS_AUTHORITY}/oauth2/v2.0/devicecode`, {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: MICROSOFT_CLIENT_ID, scope: MS_SCOPE })
  });
  const data = await readJsonResponse(response, 'Microsoft device login start');
  if (!response.ok) throw new Error(data.error_description || data.error || 'Microsoft device login could not be started');
  return data;
}
async function microsoftDevicePoll(deviceCode) {
  const response = await fetch(`${MS_AUTHORITY}/oauth2/v2.0/token`, {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: MICROSOFT_CLIENT_ID, grant_type: 'urn:ietf:params:oauth:grant-type:device_code', device_code: deviceCode })
  });
  const data = await readJsonResponse(response, 'Microsoft device token poll');
  if (response.ok) return { state: 'complete', data };
  if (data.error === 'authorization_pending') return { state: 'pending' };
  if (data.error === 'slow_down') return { state: 'slow_down' };
  if (data.error === 'expired_token') return { state: 'expired' };
  if (data.error === 'authorization_declined') return { state: 'declined' };
  return { state: 'error', message: data.error_description || data.error || 'Microsoft authentication failed' };
}
async function profileFromAccessToken(accessToken, idToken) {
  try {
    const response = await fetch('https://graph.microsoft.com/v1.0/me?$select=id,displayName,userPrincipalName,mail', { headers: { Authorization: `Bearer ${accessToken}` } });
    if (response.ok) return await readJsonResponse(response, 'Microsoft Graph profile');
  } catch {}
  if (idToken) {
    try {
      const part = String(idToken).split('.')[1]?.replace(/-/g, '+').replace(/_/g, '/');
      if (!part) return null;
      const padded = part + '='.repeat((4 - (part.length % 4)) % 4);
      const payload = JSON.parse(Buffer.from(padded, 'base64').toString('utf8'));
      return { id: payload.oid || payload.sub, displayName: payload.name || '', userPrincipalName: payload.preferred_username || '', mail: payload.email || '' };
    } catch {}
  }
  return null;
}

async function refreshMicrosoftAccessToken(refreshToken) {
  const response = await fetch(`${MS_AUTHORITY}/oauth2/v2.0/token`, {
    method: 'POST', headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ client_id: MICROSOFT_CLIENT_ID, grant_type: 'refresh_token', refresh_token: refreshToken, scope: MS_SCOPE })
  });
  const data = await readJsonResponse(response, 'Microsoft refresh token exchange');
  if (!response.ok || !data.access_token) throw new Error(data.error_description || data.error || 'Microsoft refresh failed');
  return data;
}
async function minecraftSessionFromMicrosoftAccessToken(accessToken) {
  const xbl = await fetch('https://user.auth.xboxlive.com/user/authenticate', {
    method: 'POST', headers: { 'content-type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ Properties: { AuthMethod: 'RPS', SiteName: 'user.auth.xboxlive.com', RpsTicket: `d=${accessToken}` }, RelyingParty: 'http://auth.xboxlive.com', TokenType: 'JWT' })
  });
  const xblData = await readJsonResponse(xbl, 'Xbox Live authentication');
  if (!xbl.ok || !xblData.Token) throw new Error(xblData.XErr ? `Xbox Live authentication failed (${xblData.XErr})` : 'Xbox Live authentication failed');
  const userHash = xblData.DisplayClaims?.xui?.[0]?.uhs;
  if (!userHash) throw new Error('Xbox Live user hash missing');
  const xsts = await fetch('https://xsts.auth.xboxlive.com/xsts/authorize', {
    method: 'POST', headers: { 'content-type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ Properties: { SandboxId: 'RETAIL', UserTokens: [xblData.Token] }, RelyingParty: 'rp://api.minecraftservices.com/', TokenType: 'JWT' })
  });
  const xstsData = await readJsonResponse(xsts, 'Xbox XSTS authentication');
  if (!xsts.ok || !xstsData.Token) throw new Error(xstsData.XErr ? `Xbox XSTS authentication failed (${xstsData.XErr})` : 'Xbox XSTS authentication failed');
  const mc = await fetch('https://api.minecraftservices.com/authentication/login_with_xbox', {
    method: 'POST', headers: { 'content-type': 'application/json', Accept: 'application/json' },
    body: JSON.stringify({ identityToken: `XBL3.0 x=${userHash};${xstsData.Token}`, ensureLegacyEnabled: true })
  });
  const mcData = await readJsonResponse(mc, 'Minecraft authentication');
  if (!mc.ok || !mcData.access_token) throw new Error(mcData.errorMessage || mcData.error || 'Minecraft authentication failed');
  const profileResponse = await fetch('https://api.minecraftservices.com/minecraft/profile', { headers: { Authorization: `Bearer ${mcData.access_token}` } });
  const profile = await readJsonResponse(profileResponse, 'Minecraft Java profile');
  if (!profileResponse.ok || !profile.id || !profile.name) throw new Error(profile.errorMessage || profile.error || 'Minecraft Java profile not found');
  return { accessToken: mcData.access_token, username: profile.name, uuid: profile.id, expiresIn: Number(mcData.expires_in || 86400) };
}
function launcherRateLimit(req, res) {
  const ip = String(req.ip || req.socket.remoteAddress || 'unknown');
  const entry = launcherRate.get(ip) || { count: 0, resetAt: Date.now() + LAUNCHER_WINDOW_MS };
  if (entry.resetAt <= Date.now()) { entry.count = 0; entry.resetAt = Date.now() + LAUNCHER_WINDOW_MS; }
  entry.count += 1;
  launcherRate.set(ip, entry);
  if (entry.count > MAX_LAUNCHER_ATTEMPTS) { res.status(429).json({ error: 'Te veel launcher-aanvragen. Probeer later opnieuw.' }); return false; }
  return true;
}

app.post('/v1/admin/login', (req, res) => {
  cleanupState();
  const ip = String(req.ip || req.socket.remoteAddress || 'unknown');
  const attempt = loginAttempts.get(ip) || { count: 0, resetAt: Date.now() + LOGIN_WINDOW_MS };
  if (attempt.resetAt <= Date.now()) { attempt.count = 0; attempt.resetAt = Date.now() + LOGIN_WINDOW_MS; }
  if (attempt.count >= MAX_LOGIN_ATTEMPTS) return res.status(429).json({ error: 'Te veel pogingen. Probeer later opnieuw.' });
  const username = String(req.body?.username || '').trim();
  const password = String(req.body?.password || '');
  if (!safeEqual(username, ADMIN_USERNAME) || !verifyPassword(password)) { attempt.count += 1; loginAttempts.set(ip, attempt); return res.status(401).json({ error: 'Ongeldige gebruikersnaam of wachtwoord.' }); }
  loginAttempts.delete(ip);
  const challengeId = crypto.randomUUID();
  const expiresAt = Date.now() + EMAIL_AUTH_TTL_MS;
  emailAuthChallenges.set(challengeId, { username: ADMIN_USERNAME, expiresAt });
  return res.json({ ok: true, requiresEmailCode: true, challengeId, expiresAt: new Date(expiresAt).toISOString(), allowedEmails: [...EMAIL_AUTH_ALLOWED] });
});

app.post('/v1/admin/email/request', async (req, res) => {
  cleanupState();
  const challengeId = String(req.body?.challengeId || '');
  const email = normalizeEmail(req.body?.email);
  const challenge = emailAuthChallenges.get(challengeId);
  if (!challenge || challenge.expiresAt <= Date.now()) return res.status(400).json({ error: 'De loginbevestiging is verlopen. Log opnieuw in.' });
  if (!EMAIL_AUTH_ALLOWED.has(email)) return res.status(403).json({ error: 'Dit e-mailadres is niet toegestaan voor admin-login.' });
  const key = emailRateKey(req, email);
  const current = emailAuthSendState.get(key);
  if (current && current.resetAt > Date.now()) return res.status(429).json({ error: 'Vraag over een minuut een nieuwe code aan.' });
  const code = String(crypto.randomInt(0, 1000000)).padStart(6, '0');
  emailAuthCodes.set(challengeId, { email, hash: hashAuthCode(code), expiresAt: Date.now() + EMAIL_AUTH_TTL_MS, attempts: 0 });
  emailAuthSendState.set(key, { resetAt: Date.now() + EMAIL_AUTH_COOLDOWN_MS });
  try {
    await sendAdminEmailCode(email, code);
    res.json({ ok: true, message: 'Inlogcode verzonden naar het opgegeven e-mailadres.' });
  } catch (err) {
    emailAuthCodes.delete(challengeId);
    res.status(502).json({ error: err.message || 'De e-mail kon niet worden verzonden.' });
  }
});

app.post('/v1/admin/email/verify', (req, res) => {
  cleanupState();
  const email = normalizeEmail(req.body?.email);
  const code = String(req.body?.code || '').replace(/\D/g, '').slice(0, 6);
  if (!EMAIL_AUTH_ALLOWED.has(email)) return res.status(403).json({ error: 'Dit e-mailadres is niet toegestaan voor admin-login.' });
  const stored = emailAuthCodes.get(email);
  if (!stored || stored.expiresAt <= Date.now()) {
    emailAuthCodes.delete(challengeId);
    return res.status(401).json({ error: 'De code is verlopen of bestaat niet meer.' });
  }
  if (stored.attempts >= EMAIL_AUTH_MAX_ATTEMPTS) {
    emailAuthCodes.delete(challengeId);
    return res.status(429).json({ error: 'Te veel onjuiste codes. Vraag een nieuwe code aan.' });
  }
  stored.attempts += 1;
  if (code.length !== 6 || !safeEqual(stored.hash, hashAuthCode(code))) return res.status(401).json({ error: 'Onjuiste inlogcode.' });
  emailAuthCodes.delete(challengeId);
  emailAuthChallenges.delete(challengeId);
  createAdminSession(res);
});

app.post('/v1/admin/logout', (req, res) => { const token = getSessionToken(req); if (token) sessions.delete(token); res.json({ ok: true }); });
app.get('/v1/admin/me', (req, res) => { if (!requireAdmin(req, res)) return; res.json({ authenticated: true, username: ADMIN_USERNAME }); });

app.post('/v1/admin/accounts/:id/link/start', async (req, res) => {
  if (!requireAdmin(req, res)) return;
  const id = Number(req.params.id);
  const account = db.prepare('SELECT * FROM accounts WHERE id = ?').get(id);
  if (!account) return res.status(404).json({ error: 'Account niet gevonden.' });
  if (db.prepare('SELECT 1 FROM leases WHERE account_id = ?').get(id)) return res.status(409).json({ error: 'Geef het account eerst vrij.' });
  for (const [attemptId, attempt] of linkAttempts) if (attempt.accountId === id) linkAttempts.delete(attemptId);
  try {
    const device = await microsoftDeviceStart();
    const attemptId = crypto.randomUUID();
    linkAttempts.set(attemptId, { accountId: id, deviceCode: String(device.device_code), interval: Math.max(Number(device.interval || 5), 5), nextPollAt: Date.now(), expiresAt: Date.now() + Number(device.expires_in || 900) * 1000 });
    res.json({ ok: true, attemptId, verificationUri: device.verification_uri || device.verification_url || 'https://microsoft.com/devicelogin', userCode: device.user_code, message: device.message || 'Open Microsoft login and enter the code.', expiresIn: Number(device.expires_in || 900) });
  } catch (err) { res.status(502).json({ error: err.message || 'Microsoft-login kon niet worden gestart.' }); }
});
app.post('/v1/admin/accounts/:id/link/poll', async (req, res) => {
  if (!requireAdmin(req, res)) return;
  const id = Number(req.params.id);
  const attemptId = String(req.body?.attemptId || '');
  const attempt = linkAttempts.get(attemptId);
  if (!attempt || attempt.accountId !== id) return res.status(404).json({ error: 'Koppelingssessie niet gevonden of verlopen.' });
  if (Date.now() >= attempt.expiresAt) { linkAttempts.delete(attemptId); return res.json({ state: 'expired' }); }
  if (Date.now() < attempt.nextPollAt) return res.json({ state: 'pending' });
  try {
    const result = await microsoftDevicePoll(attempt.deviceCode);
    attempt.nextPollAt = Date.now() + attempt.interval * 1000;
    if (result.state === 'pending') return res.json({ state: 'pending' });
    if (result.state === 'slow_down') { attempt.interval += 5; return res.json({ state: 'pending' }); }
    if (result.state !== 'complete') { linkAttempts.delete(attemptId); return res.json({ state: result.state, error: result.message }); }
    const profile = await profileFromAccessToken(result.data.access_token, result.data.id_token);
    const oid = String(profile?.id || '');
    if (!oid) throw new Error('Microsoft-account kon niet worden geïdentificeerd.');
    const username = String(profile.userPrincipalName || profile.mail || '');
    const owner = db.prepare('SELECT id,label FROM accounts WHERE microsoft_oid = ? AND id <> ?').get(oid, id);
    if (owner) { linkAttempts.delete(attemptId); return res.status(409).json({ state: 'conflict', error: `Dit Microsoft-account is al gekoppeld aan ${owner.label}.` }); }
    const refreshToken = String(result.data.refresh_token || '');
    if (!refreshToken) throw new Error('Microsoft gaf geen refresh-token terug; controleer of offline_access is toegestaan.');
    db.prepare('UPDATE accounts SET microsoft_oid = ?, microsoft_username = ?, microsoft_refresh_token_enc = ? WHERE id = ?').run(oid, username || null, encryptSecret(refreshToken), id);
    linkAttempts.delete(attemptId);
    res.json({ state: 'complete', account: { id, microsoftOid: oid, microsoftUsername: username } });
  } catch (err) { linkAttempts.delete(attemptId); res.status(502).json({ state: 'error', error: err.message || 'Microsoft-koppeling mislukt.' }); }
});

app.get('/v1/status', (req, res) => {
  if (!requireAdmin(req, res)) return;
  cleanupExpired();
  const rows = db.prepare(`SELECT accounts.id,accounts.slot,accounts.label,accounts.enabled,accounts.microsoft_oid,accounts.microsoft_username,
    CASE WHEN leases.id IS NULL THEN 0 ELSE 1 END AS busy,leases.client_id,leases.expires_at
    FROM accounts LEFT JOIN leases ON leases.account_id = accounts.id ORDER BY accounts.id`).all();
  res.json({ accounts: rows });
});
app.post('/v1/admin/accounts', (req, res) => {
  if (!requireAdmin(req, res)) return;
  const label = String(req.body?.label || '').trim().slice(0, 100);
  const requestedSlot = String(req.body?.slot || '').trim().slice(0, 100);
  if (!label) return res.status(400).json({ error: 'Accountnaam is verplicht.' });
  const nextNumber = Number(db.prepare('SELECT COALESCE(MAX(id),0)+1 AS n FROM accounts').get().n);
  const slot = requestedSlot || `account-${String(nextNumber).padStart(2, '0')}`;
  try {
    const info = db.prepare('INSERT INTO accounts (slot,label,enabled,created_at) VALUES (?,?,1,?)').run(slot, label, new Date().toISOString());
    res.json({ ok: true, id: Number(info.lastInsertRowid), slot, label });
  } catch (err) { res.status(409).json({ error: `Account kon niet worden toegevoegd: ${err.message}` }); }
});
app.patch('/v1/admin/accounts/:id', (req, res) => {
  if (!requireAdmin(req, res)) return;
  const id = Number(req.params.id);
  const account = db.prepare('SELECT * FROM accounts WHERE id = ?').get(id);
  if (!account) return res.status(404).json({ error: 'Account niet gevonden.' });
  const label = req.body?.label === undefined ? account.label : String(req.body.label).trim().slice(0, 100);
  const enabled = req.body?.enabled === undefined ? account.enabled : (req.body.enabled ? 1 : 0);
  if (!label) return res.status(400).json({ error: 'Accountnaam kan niet leeg zijn.' });
  db.prepare('UPDATE accounts SET label = ?, enabled = ? WHERE id = ?').run(label, enabled, id);
  res.json({ ok: true });
});
app.delete('/v1/admin/accounts/:id', (req, res) => {
  if (!requireAdmin(req, res)) return;
  const id = Number(req.params.id);
  const account = db.prepare('SELECT * FROM accounts WHERE id = ?').get(id);
  if (!account) return res.status(404).json({ error: 'Account niet gevonden.' });
  if (db.prepare('SELECT 1 FROM leases WHERE account_id = ?').get(id)) return res.status(409).json({ error: 'Account is momenteel bezet.' });
  db.prepare('DELETE FROM accounts WHERE id = ?').run(id);
  res.json({ ok: true });
});
app.post('/v1/admin/accounts/:id/unlink', (req, res) => {
  if (!requireAdmin(req, res)) return;
  const id = Number(req.params.id);
  const info = db.prepare('UPDATE accounts SET microsoft_oid = NULL, microsoft_username = NULL, microsoft_refresh_token_enc = NULL WHERE id = ?').run(id);
  if (!info.changes) return res.status(404).json({ error: 'Account niet gevonden.' });
  res.json({ ok: true });
});

app.post('/v1/launcher/lease/acquire', async (req, res) => {
  if (!launcherRateLimit(req, res)) return;
  cleanupExpired();
  const clientId = String(req.body?.clientId || '').trim().slice(0, 200);
  if (!clientId) return res.status(400).json({ error: 'clientId is required' });

  let lease;
  try {
    lease = db.transaction(() => {
      const account = db.prepare(`SELECT accounts.* FROM accounts
        WHERE accounts.enabled = 1
          AND accounts.microsoft_oid IS NOT NULL
          AND accounts.microsoft_refresh_token_enc IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM leases WHERE leases.account_id = accounts.id)
        ORDER BY accounts.id LIMIT 1`).get();
      if (!account) throw Object.assign(new Error('Geen vrij gekoppeld Minecraft-account beschikbaar.'), { status: 409 });
      const now = new Date();
      const expires = new Date(now.getTime() + DEFAULT_LEASE_SECONDS * 1000);
      const leaseId = crypto.randomUUID();
      db.prepare('INSERT INTO leases (id,account_id,client_id,acquired_at,heartbeat_at,expires_at) VALUES (?,?,?,?,?,?)').run(leaseId, account.id, clientId, now.toISOString(), now.toISOString(), expires.toISOString());
      return { leaseId, accountId: account.id, slot: account.slot, accountName: account.label, microsoftUsername: account.microsoft_username, encryptedRefreshToken: account.microsoft_refresh_token_enc, expiresAt: expires.toISOString() };
    })();
  } catch (err) {
    return res.status(err.status || 500).json({ error: err.message || 'internal error' });
  }

  try {
    const refreshToken = decryptSecret(lease.encryptedRefreshToken);
    const token = await refreshMicrosoftAccessToken(refreshToken);
    const minecraft = await minecraftSessionFromMicrosoftAccessToken(token.access_token);
    if (token.refresh_token) db.prepare('UPDATE accounts SET microsoft_refresh_token_enc = ? WHERE id = ?').run(encryptSecret(token.refresh_token), lease.accountId);
    res.json({
      leaseId: lease.leaseId,
      accountId: lease.slot,
      accountName: lease.accountName,
      microsoftUsername: lease.microsoftUsername,
      username: minecraft.username,
      uuid: minecraft.uuid,
      minecraftAccessToken: minecraft.accessToken,
      expiresIn: minecraft.expiresIn,
      expiresAt: lease.expiresAt
    });
  } catch (err) {
    db.prepare('DELETE FROM leases WHERE id = ?').run(lease.leaseId);
    res.status(502).json({ error: err.message || 'Pool-account authenticatie mislukt.' });
  }
});

app.post('/v1/launcher/lease/heartbeat', (req, res) => {
  if (!launcherRateLimit(req, res)) return;
  cleanupExpired();
  const leaseId = String(req.body?.leaseId || '');
  const clientId = String(req.body?.clientId || '').slice(0, 200);
  if (!leaseId || !clientId) return res.status(400).json({ error: 'leaseId and clientId are required' });
  const lease = db.prepare('SELECT * FROM leases WHERE id = ? AND client_id = ?').get(leaseId, clientId);
  if (!lease) return res.status(404).json({ error: 'Lease niet gevonden.' });
  const expires = new Date(Date.now() + DEFAULT_LEASE_SECONDS * 1000).toISOString();
  db.prepare('UPDATE leases SET heartbeat_at = ?, expires_at = ? WHERE id = ?').run(new Date().toISOString(), expires, leaseId);
  res.json({ ok: true, expiresAt: expires });
});
app.post('/v1/launcher/lease/release', (req, res) => {
  if (!launcherRateLimit(req, res)) return;
  const leaseId = String(req.body?.leaseId || '');
  const clientId = String(req.body?.clientId || '').slice(0, 200);
  if (!leaseId || !clientId) return res.status(400).json({ error: 'leaseId and clientId are required' });
  const info = db.prepare('DELETE FROM leases WHERE id = ? AND client_id = ?').run(leaseId, clientId);
  if (!info.changes) return res.status(404).json({ error: 'Lease niet gevonden.' });
  res.json({ ok: true });
});

// Keep legacy client-authenticated lease endpoints for compatibility.
app.post('/v1/lease/acquire', async (req, res) => {
  try {
    cleanupExpired();
    const identity = await verifyMicrosoftIdToken(req);
    const clientId = String(req.body?.clientId || '').slice(0, 200);
    if (!clientId) return res.status(400).json({ error: 'clientId is required' });
    const account = db.prepare('SELECT * FROM accounts WHERE microsoft_oid = ? AND enabled = 1').get(identity.oid);
    if (!account) return res.status(403).json({ error: 'Microsoft-account is niet gekoppeld aan een HVMC-pool-account.' });
    const active = db.prepare('SELECT * FROM leases WHERE account_id = ?').get(account.id);
    if (active) return res.status(409).json({ error: 'Account is al in gebruik.', accountName: account.label });
    const now = new Date(); const expires = new Date(now.getTime() + DEFAULT_LEASE_SECONDS * 1000); const leaseId = crypto.randomUUID();
    db.prepare('INSERT INTO leases (id,account_id,client_id,acquired_at,heartbeat_at,expires_at) VALUES (?,?,?,?,?,?)').run(leaseId,account.id,clientId,now.toISOString(),now.toISOString(),expires.toISOString());
    res.json({ leaseId, accountId: account.slot, accountName: account.label, expiresAt: expires.toISOString() });
  } catch (err) { res.status(err.status || 500).json({ error: err.message || 'internal error' }); }
});
app.post('/v1/lease/heartbeat', async (req, res) => {
  try {
    cleanupExpired(); const identity = await verifyMicrosoftIdToken(req); const leaseId = String(req.body?.leaseId || '');
    const lease = db.prepare('SELECT leases.*,accounts.microsoft_oid FROM leases JOIN accounts ON accounts.id=leases.account_id WHERE leases.id=?').get(leaseId);
    if (!lease || lease.microsoft_oid !== identity.oid) return res.status(404).json({ error: 'Lease niet gevonden.' });
    const expires = new Date(Date.now() + DEFAULT_LEASE_SECONDS * 1000).toISOString();
    db.prepare('UPDATE leases SET heartbeat_at=?,expires_at=? WHERE id=?').run(new Date().toISOString(), expires, leaseId); res.json({ ok: true, expiresAt: expires });
  } catch (err) { res.status(err.status || 500).json({ error: err.message || 'internal error' }); }
});
app.post('/v1/lease/release', async (req, res) => {
  try {
    const identity = await verifyMicrosoftIdToken(req); const leaseId = String(req.body?.leaseId || '');
    const lease = db.prepare('SELECT leases.*,accounts.microsoft_oid FROM leases JOIN accounts ON accounts.id=leases.account_id WHERE leases.id=?').get(leaseId);
    if (!lease || lease.microsoft_oid !== identity.oid) return res.status(404).json({ error: 'Lease niet gevonden.' });
    db.prepare('DELETE FROM leases WHERE id=?').run(leaseId); res.json({ ok: true });
  } catch (err) { res.status(err.status || 500).json({ error: err.message || 'internal error' }); }
});

app.listen(PORT, '127.0.0.1', () => console.log(`HVMC account-pool listening on 127.0.0.1:${PORT}`));
