// OFREP bulk evaluation under constant load.
//   k6 run -e BASE_URL=http://localhost:8080 -e API_KEY=bnt_srv_… load/k6/ofrep-bulk.js
//   -e SCENARIO=varied (default: every request has a different context, always a full evaluation)
//   -e SCENARIO=etag   (clients send If-None-Match; unchanged results answer 304)
//   -e RATE=1000 -e DURATION=60s
import http from 'k6/http';
import { check } from 'k6';
import { Counter } from 'k6/metrics';

const baseUrl = __ENV.BASE_URL || 'http://localhost:8080';
const apiKey = __ENV.API_KEY;
const scenario = __ENV.SCENARIO || 'varied';
const rate = Number(__ENV.RATE || 1000);
const duration = __ENV.DURATION || '60s';
const notModified = new Counter('not_modified');

export const options = {
  scenarios: {
    warmup: { executor: 'constant-arrival-rate', rate: 200, timeUnit: '1s', duration: '10s', preAllocatedVUs: 50, maxVUs: 200, exec: 'run', startTime: '0s' },
    load: { executor: 'constant-arrival-rate', rate, timeUnit: '1s', duration, preAllocatedVUs: 100, maxVUs: 1000, exec: 'run', startTime: '12s' },
  },
  thresholds: {
    'http_req_duration{scenario:load}': ['p(99)<20'],
    'http_req_failed{scenario:load}': ['rate<0.001'],
    'checks{scenario:load}': ['rate>0.999'],
  },
  summaryTrendStats: ['avg', 'min', 'med', 'p(90)', 'p(95)', 'p(99)', 'p(99.9)', 'max'],
};

const plans = ['free', 'pro', 'team', 'enterprise'];
const countries = ['TR', 'DE', 'FR', 'US', 'NL', 'JP', 'BR', 'ES'];
const versions = ['2.1.0', '2.3.0', '2.4.1', '3.0.0'];
const pick = (list) => list[Math.floor(Math.random() * list.length)];
const etags = {};

export function run() {
  const headers = { 'Content-Type': 'application/json', 'X-API-Key': apiKey };
  let context;
  if (scenario === 'etag') {
    // 100 distinct users, each polling repeatedly: after the first answer, results are unchanged.
    const n = Math.floor(Math.random() * 100);
    context = { targetingKey: `user-${n}`, plan: plans[n % 4], country: countries[n % 8], appVersion: versions[n % 4] };
    if (etags[n]) headers['If-None-Match'] = etags[n];
    const response = http.post(`${baseUrl}/ofrep/v1/evaluate/flags`, JSON.stringify({ context }), { headers });
    if (response.status === 304) notModified.add(1);
    else if (response.headers.Etag) etags[n] = response.headers.Etag;
    check(response, { 'status 200 or 304': (r) => r.status === 200 || r.status === 304 });
    return;
  }
  context = {
    targetingKey: `user-${Math.floor(Math.random() * 1_000_000)}`,
    plan: pick(plans),
    country: pick(countries),
    appVersion: pick(versions),
    beta: Math.random() < 0.1,
  };
  const response = http.post(`${baseUrl}/ofrep/v1/evaluate/flags`, JSON.stringify({ context }), { headers });
  check(response, {
    'status 200': (r) => r.status === 200,
    'all flags evaluated': (r) => r.status === 200 && r.json('flags').length === 50,
  });
}
