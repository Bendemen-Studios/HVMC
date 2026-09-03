import express from 'express';
import crypto from 'node:crypto';
import nodemailer from 'nodemailer';

const allowedEmails = new Set(['info@bendemen.nl', 'bendemenbv@gmail.com']);
const challenges = new Map();
const pendingCodes = new Map();
const REQUEST_TTL_MS = 10 * 60 * 1000;
const CODE_MAX_ATTEMPTS = 5;
const CODE_COOLDOWN_MS = 60 * 1000;

const env = (name, fallback = '') => String(process.env[name] ?? fallback).trim();
const smtpHost = env('EMAIL_SMTP_HOST');
const smtpPort = Number(env('EMAIL_SMTP_PORT', '587'));
const smtpSecure = env('EMAIL_SMTP_SECURE', 'false').toLowerCase() === 'true';
const smtpUser = env('EMAIL_SMTP_USER');
const smtpPassword = env('EMAIL_SMTP_PASSWORD');
const emailFrom = env('EMAIL_FROM', 'automail@hvmc.nl');
const emailFromName = env('EMAIL_FROM_NAME', 'HVMC Account Dashboard');

let transporter = null;
if (smtpHost) {
  transporter = nodemailer.createTransport({
    host: smtpHost,
    port: smtpPort,
    secure: smtpSecure,
    auth: smtpUser ? { user: smtpUser, pass: smtpPassword } : undefined
  });
}

const randomCode = () => String(crypto.randomInt(0, 1_000_000)).padStart(6, '0');
const hashCode = (challengeId, code) => crypto.createHash('sha256').update(`${challengeId}:${code}`).digest('hex');
const cleanup = () => {
  const now = Date.now();
  for (const [id, value] of challenges) if (value.expiresAt <= now) challenges.delete(id);
  for (const [id, value] of pendingCodes) if (value.expiresAt <= now) pendingCodes.delete(id);
};
setInterval(cleanup, 60_000).unref();

// Compatibility fix for the current admin-session parser: normalize only
// admin requests so their Bearer token reaches the session map unchanged.
// Do not touch launcher/Microsoft bearer tokens used by other endpoints.
const originalUse = express.application.use;
express.application.use = function patchedUse(...args) {
  if (!this.__hvmcAdminAuthBridgeInstalled) {
    this.__hvmcAdminAuthBridgeInstalled = true;
    originalUse.call(this, (req, _res, next) => {
      if (String(req.path || '').startsWith('/v1/admin/') || req.path === '/v1/status') {
        const authorization = String(req.headers.authorization || '');
        if (/^Bearer\s+/i.test(authorization)) {
          req.headers.authorization = authorization.replace(/^Bearer\s+/i, '').trim();
        }
      }
      next();
    });
  }
  return originalUse.apply(this, args);
};

const originalPost = express.application.post;
express.application.post = function patchedPost(path, ...handlers) {
  if (path !== '/v1/admin/login') return originalPost.call(this, path, ...handlers);

  const wrapped = async (req, res, next) => {
    let payloadSent = false;
    const originalJson = res.json.bind(res);
    res.json = (payload) => {
      if (!payloadSent && payload?.ok && payload?.token && payload?.expiresAt) {
        payloadSent = true;
        const challengeId = crypto.randomUUID();
        challenges.set(challengeId, {
          token: String(payload.token),
          username: String(req.body?.username || ''),
          expiresAt: Date.now() + REQUEST_TTL_MS,
          ip: String(req.ip || req.socket.remoteAddress || '')
        });
        return originalJson({
          ok: true,
          requiresEmailCode: true,
          challengeId,
          expiresAt: new Date(Date.now() + REQUEST_TTL_MS).toISOString(),
          allowedEmails: [...allowedEmails]
        });
      }
      payloadSent = true;
      return originalJson(payload);
    };

    let index = 0;
    const run = (err) => {
      if (err) return next(err);
      const handler = handlers[index++];
      if (!handler) return next();
      try {
        const result = handler(req, res, run);
        if (result?.catch) result.catch(next);
      } catch (error) {
        next(error);
      }
    };
    run();
  };

  return originalPost.call(this, path, wrapped);
};

const originalListen = express.application.listen;
express.application.listen = function patchedListen(...args) {
  this.post('/v1/admin/email/request', async (req, res) => {
    cleanup();
    const challengeId = String(req.body?.challengeId || '');
    const email = String(req.body?.email || '').trim().toLowerCase();
    const challenge = challenges.get(challengeId);
    if (!challenge || challenge.expiresAt <= Date.now()) return res.status(400).json({ error: 'Loginbevestiging is verlopen. Log opnieuw in met je gebruikersnaam en wachtwoord.' });
    if (!allowedEmails.has(email)) return res.status(403).json({ error: 'Dit e-mailadres is niet toegestaan voor beheerderstoegang.' });

    const previous = pendingCodes.get(challengeId);
    if (previous && previous.sentAt + CODE_COOLDOWN_MS > Date.now()) {
      const seconds = Math.ceil((previous.sentAt + CODE_COOLDOWN_MS - Date.now()) / 1000);
      return res.status(429).json({ error: `Vraag over ${seconds} seconden een nieuwe code aan.` });
    }
    if (!transporter) return res.status(503).json({ error: 'E-mailverzending is nog niet geconfigureerd op de server.' });

    const code = randomCode();
    pendingCodes.set(challengeId, {
      email,
      codeHash: hashCode(challengeId, code),
      expiresAt: Date.now() + REQUEST_TTL_MS,
      sentAt: Date.now(),
      attempts: 0
    });

    try {
      await transporter.sendMail({
        from: { name: emailFromName, address: emailFrom },
        to: email,
        subject: 'HVMC Account Dashboard',
        text: `Je HVMC Account Dashboard-verificatiecode is ${code}. Deze code is 10 minuten geldig.`,
        html: `<p>Je HVMC Account Dashboard-verificatiecode is:</p><p style="font-size:28px;font-weight:700;letter-spacing:6px">${code}</p><p>Deze code is 10 minuten geldig.</p>`
      });
      res.json({ ok: true, expiresAt: new Date(Date.now() + REQUEST_TTL_MS).toISOString() });
    } catch (error) {
      pendingCodes.delete(challengeId);
      res.status(502).json({ error: `E-mail kon niet worden verzonden: ${error.message || 'onbekende SMTP-fout'}` });
    }
  });

  this.post('/v1/admin/email/verify', (req, res) => {
    cleanup();
    const challengeId = String(req.body?.challengeId || '');
    const email = String(req.body?.email || '').trim().toLowerCase();
    const code = String(req.body?.code || '').trim();
    const challenge = challenges.get(challengeId);
    const pending = pendingCodes.get(challengeId);
    if (!challenge || challenge.expiresAt <= Date.now()) return res.status(400).json({ error: 'Loginbevestiging is verlopen. Log opnieuw in.' });
    if (!pending || pending.expiresAt <= Date.now() || pending.email !== email) return res.status(400).json({ error: 'Verificatiecode ontbreekt of is verlopen.' });
    if (!/^\d{6}$/.test(code)) return res.status(400).json({ error: 'Vul een geldige 6-cijferige code in.' });
    pending.attempts += 1;
    if (pending.attempts > CODE_MAX_ATTEMPTS) {
      challenges.delete(challengeId);
      pendingCodes.delete(challengeId);
      return res.status(429).json({ error: 'Te veel onjuiste codes. Log opnieuw in.' });
    }
    if (hashCode(challengeId, code) !== pending.codeHash) return res.status(401).json({ error: 'Onjuiste verificatiecode.' });

    challenges.delete(challengeId);
    pendingCodes.delete(challengeId);
    res.json({ ok: true, token: challenge.token, username: challenge.username });
  });

  return originalListen.apply(this, args);
};
