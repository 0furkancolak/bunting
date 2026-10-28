// Creates a demo project with two flags and prints a server key for the development environment.
//   node samples/seed.mjs [serverUrl] [adminEmail] [adminPassword]
// Defaults match docker-compose.yml. Needs Node 18+ and no dependencies.
const [baseUrl = 'http://localhost:8080', email = 'admin@bunting.local', password = 'change-me-please'] = process.argv.slice(2);
const project = 'demo';

async function call(method, path, token, body, headers = {}) {
  const response = await fetch(`${baseUrl}/api/v1${path}`, {
    method,
    headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}), ...headers },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  return { status: response.status, etag: response.headers.get('etag'), body: text ? JSON.parse(text) : null };
}

const login = await call('POST', '/auth/login', null, { email, password });
if (login.status !== 200) {
  console.error(`Sign-in failed (${login.status}). Is the server running at ${baseUrl} and are the credentials right?`);
  process.exit(1);
}
const token = login.body.accessToken;

const created = await call('POST', '/projects', token, { key: project, name: 'Demo shop' });
console.log(created.status === 201 ? `Created project "${project}"` : `Project "${project}" already exists`);

async function flag(key, name, type, variants, defaultVariant, config) {
  const r = await call('POST', `/projects/${project}/flags`, token, { key, name, type, variants, defaultVariant, clientSideAvailable: true });
  if (r.status === 201) {
    const current = await call('GET', `/projects/${project}/environments/development/flags/${key}`, token);
    const put = await call('PUT', `/projects/${project}/environments/development/flags/${key}`, token, config, { 'if-match': current.etag });
    if (put.status !== 200) throw new Error(`Configuring ${key} failed: ${JSON.stringify(put.body)}`);
    console.log(`Created flag "${key}"`);
  } else {
    console.log(`Flag "${key}" already exists`);
  }
}

await flag('new-checkout', 'New checkout', 'Boolean', { on: true, off: false }, 'off', {
  enabled: true,
  offVariant: 'off',
  rules: [{ id: 'pro-users', description: 'Pro plan', conditions: [{ attribute: 'plan', operator: 'Equals', values: ['pro'] }], serve: { variant: 'on' } }],
  fallthrough: { rollout: { variants: [{ variant: 'on', weight: 2000 }, { variant: 'off', weight: 8000 }] } },
});
await flag('banner-text', 'Banner text', 'String', { welcome: 'Welcome!', sale: 'Spring sale — 20% off' }, 'welcome', {
  enabled: true,
  offVariant: 'welcome',
  rules: [],
  fallthrough: { variant: 'sale' },
});

const key = await call('POST', `/projects/${project}/environments/development/api-keys`, token, { name: `sample-${Date.now()}`, type: 'Server' });
console.log('\nServer key for the development environment (shown once):\n');
console.log(`  ${key.body.key}\n`);
console.log('Try it:');
console.log(`  BUNTING_URL=${baseUrl} BUNTING_API_KEY=${key.body.key} node samples/Bunting.Samples.Node/index.mjs`);
console.log(`  dotnet run --project samples/Bunting.Samples.WebApi   (set Bunting__ServerUrl and Bunting__ApiKey)`);
