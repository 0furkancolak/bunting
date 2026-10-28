// Creates a project "load" with a realistic production ruleset (50 flags, 3 segments) and prints shell exports.
//   eval "$(node load/seed.mjs http://localhost:8080 admin@bunting.local change-me-please)"
const [baseUrl = 'http://localhost:8080', email = 'admin@bunting.local', password = 'change-me-please'] = process.argv.slice(2);
const project = 'load';
const env = 'production';

async function call(method, path, token, body, headers = {}) {
  const response = await fetch(`${baseUrl}/api/v1${path}`, {
    method,
    headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  return { status: response.status, etag: response.headers.get('etag'), body: text ? JSON.parse(text) : null };
}
function must(r, what) {
  if (r.status >= 300) throw new Error(`${what} failed: ${r.status} ${JSON.stringify(r.body)}`);
  return r;
}

const token = must(await call('POST', '/auth/login', null, { email, password }), 'login').body.accessToken;
const existing = await call('GET', `/projects/${project}`, token);
if (existing.status === 200) await call('DELETE', `/projects/${project}`, token);
must(await call('POST', '/projects', token, { key: project, name: 'Load test' }), 'create project');

for (const [key, conditions] of [
  ['beta-testers', [{ attribute: 'plan', operator: 'In', values: ['pro', 'team'] }]],
  ['eu', [{ attribute: 'country', operator: 'In', values: ['DE', 'FR', 'NL', 'ES', 'IT', 'PL'] }]],
  ['recent-app', [{ attribute: 'appVersion', operator: 'SemVerGreaterThan', values: ['2.3.0'] }]],
]) {
  must(await call('POST', `/projects/${project}/environments/${env}/segments`, token, { key, included: ['vip-1', 'vip-2'], excluded: [], conditions }), `segment ${key}`);
}

// 50 flags: boolean targeting, percentage rollouts, strings, numbers, JSON, and a few switched off.
for (let i = 0; i < 50; i++) {
  const kind = i % 5;
  const key = `flag-${String(i).padStart(2, '0')}`;
  const [type, variants, def, rules, fallthrough] = [
    ['Boolean', { on: true, off: false }, 'off',
      [{ id: 'segment', conditions: [{ attribute: '', operator: 'InSegment', values: ['beta-testers'] }], serve: { variant: 'on' } },
       { id: 'country', conditions: [{ attribute: 'country', operator: 'In', values: ['TR', 'DE'] }, { attribute: 'plan', operator: 'Equals', values: ['free'], negate: true }], serve: { variant: 'on' } }],
      { variant: 'off' }],
    ['Boolean', { on: true, off: false }, 'off', [],
      { rollout: { variants: [{ variant: 'on', weight: 2500 }, { variant: 'off', weight: 7500 }] } }],
    ['String', { a: 'alpha', b: 'beta', c: 'gamma' }, 'a',
      [{ id: 'eu', conditions: [{ attribute: '', operator: 'InSegment', values: ['eu'] }], serve: { variant: 'b' } }],
      { rollout: { variants: [{ variant: 'a', weight: 5000 }, { variant: 'b', weight: 3000 }, { variant: 'c', weight: 2000 }] } }],
    ['Integer', { small: 10, large: 100 }, 'small',
      [{ id: 'recent', conditions: [{ attribute: '', operator: 'InSegment', values: ['recent-app'] }, { attribute: 'targetingKey', operator: 'StartsWith', values: ['user-1'] }], serve: { variant: 'large' } }],
      { variant: 'small' }],
    ['Object', { off: { limit: 1 }, on: { limit: 50, tiers: ['a', 'b'] } }, 'off',
      [{ id: 'pro', conditions: [{ attribute: 'plan', operator: 'Equals', values: ['pro'] }], serve: { variant: 'on' } }],
      { variant: 'off' }],
  ][kind];
  must(await call('POST', `/projects/${project}/flags`, token, { key, name: key, type, variants, defaultVariant: def, clientSideAvailable: i % 2 === 0 }), `flag ${key}`);
  const current = must(await call('GET', `/projects/${project}/environments/${env}/flags/${key}`, token), `get ${key}`);
  must(await call('PUT', `/projects/${project}/environments/${env}/flags/${key}`, token, { enabled: i % 10 !== 9, offVariant: def, rules, fallthrough }, { 'if-match': current.etag }), `config ${key}`);
}

const server = must(await call('POST', `/projects/${project}/environments/${env}/api-keys`, token, { name: 'load-server', type: 'Server' }), 'server key').body.key;
const client = must(await call('POST', `/projects/${project}/environments/${env}/api-keys`, token, { name: 'load-client', type: 'Client' }), 'client key').body.key;
console.log(`export BASE_URL=${baseUrl} ADMIN_EMAIL=${email} ADMIN_PASSWORD=${password} PROJECT=${project} ENVIRONMENT=${env}`);
console.log(`export SERVER_KEY=${server} CLIENT_KEY=${client}`);
