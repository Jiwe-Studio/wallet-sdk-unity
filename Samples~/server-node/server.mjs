// A server in front of Jiwe — the shape Sheng Word League uses. No dependencies; Node 18+.
//
//   JIWE_API_USERNAME=... JIWE_API_KEY=... node server.mjs
//
// Why a server: your Jiwe API key lives HERE, not in the shipped game, and the server decides how much
// to pay before calling Jiwe. A client-only game can't enforce a cap — a modified client skips it.
//
// What this sample does when a game reports a finished run:
//   1. checks the score against a rule the server owns (in a real game, validate the actual run),
//   2. RESERVES the payout before the first `await`, so two simultaneous requests can't both take it,
//   3. credits the player on Jiwe (their own token goes in X-API-TOKEN, so Jiwe credits the right account),
//   4. RELEASES the reservation if Jiwe fails, so the player isn't charged against their cap for nothing.
//
// Caps are keyed by the token's `sub` (a stable identifier) — never by phone number or email. Sheng Word League
// once keyed airtime claims by phone, and one account claimed twice for the same puzzle.
//
// `sub` is read from the ID token WITHOUT verifying its signature here, only to key the cap. Jiwe verifies the
// token when you credit the player, so a forged token can't earn anything. For any other use of the token,
// verify it against the issuer's JWKS (see developers.jiwe.io, Discovery).
import http from 'node:http';

const PORT = Number(process.env.PORT ?? 8766);
const JIWE_API_BASE = (process.env.JIWE_API_BASE ?? 'https://abacus.jiwe.io/rest/api/v1').replace(/\/$/, '');
const API_USERNAME = process.env.JIWE_API_USERNAME;
const API_KEY = process.env.JIWE_API_KEY;
const DAILY_CAP_XP = Number(process.env.DAILY_CAP_XP ?? 500);
const MAX_XP_PER_RUN = Number(process.env.MAX_XP_PER_RUN ?? 200);

if (!API_USERNAME || !API_KEY) {
  console.error('Set JIWE_API_USERNAME and JIWE_API_KEY (from your Jiwe app).');
  process.exit(1);
}

// In memory for the sample. Use a database in production, and persist after every reserve/release so a
// restart can't reset who has been paid.
const paid = new Map(); // "YYYY-MM-DD:sub" -> XP paid today
const today = () => new Date().toISOString().slice(0, 10);

function subFromIdToken(token) {
  const part = token.split('.')[1];
  if (!part) throw new Error('malformed token');
  const padded = part.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(part.length / 4) * 4, '=');
  const sub = JSON.parse(Buffer.from(padded, 'base64').toString('utf8')).sub;
  if (!sub) throw new Error('token has no sub');
  return sub;
}

// Retry guidance from Jiwe's Errors page: 429, 5xx and "no response" are worth retrying; other 4xx are final.
const isRetryable = (status) => status === 0 || status === 429 || status >= 500;

function send(res, status, body) {
  res.writeHead(status, {
    'Content-Type': 'application/json',
    'Access-Control-Allow-Origin': '*', // dev convenience for WebGL; restrict to your game's origin in production
    'Access-Control-Allow-Headers': '*',
  });
  res.end(JSON.stringify(body));
}

async function readJson(req) {
  let raw = '';
  for await (const chunk of req) raw += chunk;
  try { return raw ? JSON.parse(raw) : {}; } catch { return null; }
}

async function runComplete(req, res) {
  const token = (req.headers.authorization ?? '').replace(/^Bearer /i, '');
  let sub;
  try { sub = subFromIdToken(token); } catch { return send(res, 401, { error: 'sign in first' }); }

  const body = await readJson(req);
  const score = body?.score;
  if (!Number.isInteger(score) || score < 1 || score > MAX_XP_PER_RUN)
    return send(res, 400, { error: `score must be a whole number from 1 to ${MAX_XP_PER_RUN}`, retryable: false });

  const key = `${today()}:${sub}`;
  const amount = Math.min(score, DAILY_CAP_XP - (paid.get(key) ?? 0));
  if (amount <= 0) return send(res, 200, { awarded: 0, capReached: true });

  paid.set(key, (paid.get(key) ?? 0) + amount);          // RESERVE — before any await

  let status = 0, data = {};
  try {
    const jiwe = await fetch(`${JIWE_API_BASE}/rewards/xp`, {
      method: 'POST',
      headers: { 'X-API-USERNAME': API_USERNAME, 'X-API-KEY': API_KEY, 'X-API-TOKEN': token, 'Content-Type': 'application/json' },
      body: JSON.stringify({ xpEarned: amount, description: 'Run complete', gameSessionId: body.runId ?? 'server', metadata: {} }),
    });
    status = jiwe.status;
    data = await jiwe.json().catch(() => ({}));
    if (jiwe.ok && data.type !== 'ERROR')
      return send(res, 200, { awarded: amount, ledger_transaction_id: data.ledger_transaction_id });
  } catch (err) {
    status = 0;                                          // no response: the outcome is unknown
  }

  paid.set(key, paid.get(key) - amount);                  // RELEASE — the player wasn't credited
  console.error(`Jiwe XP credit failed: HTTP ${status}`, data?.message ?? '');
  // Keep Jiwe's raw message in YOUR log; give the player friendly wording.
  return send(res, 502, { error: 'We couldn\'t save your XP right now.', retryable: isRetryable(status) });
}

const server = http.createServer(async (req, res) => {
  if (req.method === 'OPTIONS') return send(res, 204, {});
  if (req.method === 'POST' && req.url === '/api/run-complete') return runComplete(req, res);
  if (req.method === 'GET' && req.url === '/api/health') return send(res, 200, { ok: true });
  if (req.method === 'POST' && req.url === '/api/_reset' && process.env.ALLOW_TEST_RESET === '1') { paid.clear(); return send(res, 200, { ok: true }); }
  send(res, 404, { error: 'not found' });
});

server.listen(PORT, () => console.log(`Jiwe server sample on http://localhost:${PORT} (cap ${DAILY_CAP_XP} XP/day, ${MAX_XP_PER_RUN}/run)`));
