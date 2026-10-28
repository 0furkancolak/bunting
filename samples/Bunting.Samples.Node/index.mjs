// Bunting speaks OFREP, so Node needs no Bunting-specific client: the standard OpenFeature SDK plus the OFREP provider.
//   BUNTING_URL=http://localhost:8080 BUNTING_API_KEY=bnt_srv_… node index.mjs
import { OpenFeature } from '@openfeature/server-sdk';
import { OFREPProvider } from '@openfeature/ofrep-provider';

const baseUrl = process.env.BUNTING_URL ?? 'http://localhost:8080';
const apiKey = process.env.BUNTING_API_KEY;
if (!apiKey) {
  console.error('Set BUNTING_API_KEY to a server key (run `node ../seed.mjs` to create one).');
  process.exit(1);
}

await OpenFeature.setProviderAndWait(new OFREPProvider({ baseUrl, headers: [['X-API-Key', apiKey]] }));
const flags = OpenFeature.getClient();

const users = [
  { targetingKey: 'alice', plan: 'pro' },
  { targetingKey: 'bob', plan: 'free' },
  { targetingKey: 'carol', plan: 'free' },
];

console.log('Evaluating every 2 seconds. Change a flag in the admin panel and watch the output follow. Ctrl+C to stop.\n');
for (;;) {
  for (const context of users) {
    const checkout = await flags.getBooleanDetails('new-checkout', false, context);
    const banner = await flags.getStringValue('banner-text', 'Hello', context);
    console.log(`${context.targetingKey.padEnd(6)} new-checkout=${checkout.value} (${checkout.reason}${checkout.flagMetadata?.ruleId ? `, rule ${checkout.flagMetadata.ruleId}` : ''})  banner="${banner}"`);
  }
  console.log();
  await new Promise((resolve) => setTimeout(resolve, 2000));
}
