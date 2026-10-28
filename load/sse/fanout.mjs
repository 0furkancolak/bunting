// Opens many SSE connections to /api/v1/sdk/stream, holds them, then changes a flag and measures how long the
// change takes to reach every connection.
//   eval "$(node load/seed.mjs)"
//   node load/sse/fanout.mjs --connections 5000 --hold 60 --rounds 5
// Options: --connections N  --ramp PER_SECOND  --hold SECONDS  --rounds N  --container NAME (for memory stats)
import http from 'node:http';
import { execFileSync } from 'node:child_process';

const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const baseUrl = process.env.BASE_URL ?? 'http://localhost:8080';
const key = process.env.SERVER_KEY;
const email = process.env.ADMIN_EMAIL ?? 'admin@bunting.local';
const password = process.env.ADMIN_PASSWORD ?? 'change-me-please';
const project = process.env.PROJECT ?? 'load';
const environment = process.env.ENVIRONMENT ?? 'production';
const target = Number(arg('connections', 5000));
const ramp = Number(arg('ramp', 250));
const holdSeconds = Number(arg('hold', 60));
const rounds = Number(arg('rounds', 5));
const container = arg('container', 'bunting-server-1');
if (!key) throw new Error('Set SERVER_KEY (run: eval "$(node load/seed.mjs)")');

const url = new URL(baseUrl);
const agent = new http.Agent({ keepAlive: true, maxSockets: Infinity });
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const percentile = (sorted, p) => sorted[Math.min(sorted.length - 1, Math.floor((p / 100) * sorted.length))];
const dockerStats = () => {
  try {
    return execFileSync('docker', ['stats', '--no-stream', '--format', '{{.CPUPerc}} cpu, {{.MemUsage}}', container], { encoding: 'utf8' }).trim();
  } catch {
    return 'n/a';
  }
};

// --- management API (to change a flag) ---
async function api(method, path, token, body, headers = {}) {
  const r = await fetch(`${baseUrl}/api/v1${path}`, {
    method,
    headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await r.text();
  return { status: r.status, etag: r.headers.get('etag'), body: text ? JSON.parse(text) : null };
}
const token = (await api('POST', '/auth/login', null, { email, password })).body.accessToken;
const flagPath = `/projects/${project}/environments/${environment}/flags/flag-00`;
async function toggle() {
  const current = await api('GET', flagPath, token);
  const r = await api('PUT', flagPath, token,
    { enabled: !current.body.enabled, offVariant: current.body.offVariant, rules: current.body.rules, fallthrough: current.body.fallthrough },
    { 'if-match': current.etag });
  if (r.status !== 200) throw new Error(`toggle failed: ${r.status} ${JSON.stringify(r.body)}`);
}

// --- connections ---
const connections = [];
let connected = 0;
let failed = 0;
let dropped = 0;
let heartbeats = 0;
const connectTimes = [];
let round = null; // { t0, arrivals: [] }

function open(index) {
  const started = performance.now();
  const req = http.request({ host: url.hostname, port: url.port, path: '/api/v1/sdk/stream', agent, headers: { 'X-API-Key': key, Accept: 'text/event-stream' } });
  const c = { index, version: 0, heartbeats: 0, buffer: '', open: false };
  connections.push(c);
  req.on('response', (res) => {
    if (res.statusCode !== 200) {
      failed++;
      res.resume();
      return;
    }
    res.setEncoding('utf8');
    res.on('data', (chunk) => {
      c.buffer += chunk;
      let end;
      while ((end = c.buffer.indexOf('\n\n')) >= 0) {
        const message = c.buffer.slice(0, end);
        c.buffer = c.buffer.slice(end + 2);
        const name = /event: (.+)/.exec(message)?.[1];
        if (name === 'ruleset-updated') {
          const version = JSON.parse(/data: (.+)/.exec(message)[1]).version;
          if (!c.open) {
            c.open = true;
            connected++;
            connectTimes.push(performance.now() - started);
          } else if (round && version > c.version) {
            round.arrivals.push(performance.now() - round.t0);
          }
          c.version = version;
        } else if (name === 'heartbeat') {
          heartbeats++;
          c.heartbeats++;
        }
      }
    });
    res.on('close', () => {
      if (c.open) dropped++;
      c.open = false;
    });
  });
  req.on('error', () => {
    failed++;
  });
  req.end();
}

console.log(`Opening ${target} connections at ${ramp}/s against ${baseUrl} …`);
const rampStart = performance.now();
for (let i = 0; i < target; i += ramp) {
  for (let j = i; j < Math.min(i + ramp, target); j++) open(j);
  await sleep(1000);
}
const deadline = performance.now() + 30000;
while (connected + failed < target && performance.now() < deadline) await sleep(100);
console.log(`Connected ${connected}/${target} (failed ${failed}) in ${((performance.now() - rampStart) / 1000).toFixed(1)} s`);
connectTimes.sort((a, b) => a - b);
console.log(`Time to first event: p50 ${percentile(connectTimes, 50).toFixed(0)} ms, p99 ${percentile(connectTimes, 99).toFixed(0)} ms`);
console.log(`Server after ramp: ${dockerStats()}`);

console.log(`Holding for ${holdSeconds} s (heartbeats every 25 s) …`);
await sleep(holdSeconds * 1000);
const stillOpen = connections.filter((c) => c.open).length;
const withHeartbeat = connections.filter((c) => c.heartbeats > 0).length;
console.log(`After hold: ${stillOpen} open, ${dropped} dropped, ${withHeartbeat} received a heartbeat, ${heartbeats} heartbeats in total`);
console.log(`Server while holding: ${dockerStats()}`);

const all = [];
let missingTotal = 0;
for (let r = 1; r <= rounds; r++) {
  round = { t0: performance.now(), arrivals: [] };
  await toggle();
  const putDone = performance.now() - round.t0;
  const waitUntil = performance.now() + 15000;
  while (round.arrivals.length < stillOpen && performance.now() < waitUntil) await sleep(10);
  const sorted = [...round.arrivals].sort((a, b) => a - b);
  const missing = stillOpen - sorted.length;
  missingTotal += missing;
  all.push(...sorted);
  console.log(`Round ${r}: change API call took ${putDone.toFixed(0)} ms; delivered to ${sorted.length}/${stillOpen}; from change start: p50 ${percentile(sorted, 50).toFixed(0)} ms, p95 ${percentile(sorted, 95).toFixed(0)} ms, p99 ${percentile(sorted, 99).toFixed(0)} ms, max ${sorted.at(-1)?.toFixed(0)} ms`);
  round = null;
  await sleep(1000);
}
all.sort((a, b) => a - b);
console.log(`\nAll rounds: ${all.length} deliveries, ${missingTotal} missing; p50 ${percentile(all, 50).toFixed(0)} ms, p95 ${percentile(all, 95).toFixed(0)} ms, p99 ${percentile(all, 99).toFixed(0)} ms, max ${all.at(-1)?.toFixed(0)} ms`);
console.log(`Server at the end: ${dockerStats()}`);
process.exit(missingTotal === 0 && dropped === 0 && failed === 0 ? 0 : 1);
