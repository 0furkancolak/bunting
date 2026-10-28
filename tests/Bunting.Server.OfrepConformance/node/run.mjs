// Drives Bunting through the official OpenFeature JS SDKs and OFREP providers and prints the observed results as JSON.
// usage: node run.mjs <baseUrl> <serverKey> <clientKey> <badKey>
import { OpenFeature } from '@openfeature/server-sdk';
import { OFREPProvider } from '@openfeature/ofrep-provider';
import { OpenFeature as WebOpenFeature } from '@openfeature/web-sdk';
import { OFREPWebProvider } from '@openfeature/ofrep-web-provider';

const [baseUrl, serverKey, clientKey, badKey] = process.argv.slice(2);
const out = { server: {}, web: {}, badKey: {} };

const detail = (d) => ({
  value: d.value,
  reason: d.reason ?? null,
  variant: d.variant ?? null,
  errorCode: d.errorCode ?? null,
  ruleId: d.flagMetadata?.ruleId ?? null,
});

// --- server-side provider: one request per evaluation, full context, server key ---
await OpenFeature.setProviderAndWait(new OFREPProvider({ baseUrl, headers: [['X-API-Key', serverKey]] }));
const server = OpenFeature.getClient();
const tr = { targetingKey: 'user-1', country: 'TR' };
const de = { targetingKey: 'user-1', country: 'DE' };
out.server.boolMatch = detail(await server.getBooleanDetails('promo', false, tr));
out.server.boolDefault = detail(await server.getBooleanDetails('promo', true, de));
out.server.string = detail(await server.getStringDetails('title', 'fallback', tr));
out.server.integer = detail(await server.getNumberDetails('limit', -1, tr));
out.server.double = detail(await server.getNumberDetails('ratio', -1, tr));
out.server.object = detail(await server.getObjectDetails('config', { fallback: true }, tr));
out.server.missing = detail(await server.getBooleanDetails('does-not-exist', true, tr));
out.server.typeMismatch = detail(await server.getStringDetails('promo', 'code-default', tr));
out.server.noTargetingKey = detail(await server.getBooleanDetails('gradual', true, { country: 'DE' }));
out.server.gradual = detail(await server.getBooleanDetails('gradual', false, { targetingKey: 'user-1' }));
await OpenFeature.close();

// --- an invalid key must degrade to code defaults, not throw ---
try {
  await OpenFeature.setProviderAndWait(new OFREPProvider({ baseUrl, headers: [['X-API-Key', badKey]] }));
} catch (e) {
  out.badKey.initError = String(e?.message ?? e);
}
const bad = OpenFeature.getClient();
out.badKey.evaluation = detail(await bad.getBooleanDetails('promo', true, tr));
await OpenFeature.close();

// --- web (static-context) provider: bulk evaluation with a client key ---
await WebOpenFeature.setContext({ targetingKey: 'user-1', country: 'TR' });
await WebOpenFeature.setProviderAndWait(
  new OFREPWebProvider({ baseUrl, headers: [['X-API-Key', clientKey]], cacheMode: 'disabled', changeDetection: 'none' }),
);
const web = WebOpenFeature.getClient();
out.web.visible = detail(web.getBooleanDetails('visible', false));
out.web.hidden = detail(web.getBooleanDetails('internal', true));
out.web.missing = detail(web.getBooleanDetails('does-not-exist', true));
await WebOpenFeature.setContext({ targetingKey: 'user-2', country: 'DE' });
out.web.afterContextChange = detail(web.getBooleanDetails('visible', true));
await WebOpenFeature.close();

console.log(JSON.stringify(out));
process.exit(0);
