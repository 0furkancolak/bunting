#!/usr/bin/env bash
# Runs the OFREP bulk scenario at several rates and prints a Markdown table.
#   eval "$(node load/seed.mjs)"; load/run-ofrep.sh            # rates 1000 2000 4000 5000, 30 s each
#   RATES="1000" DURATION=60s CONTAINER=bunting-server-1 load/run-ofrep.sh
set -euo pipefail
: "${BASE_URL:?run  eval \"\$(node load/seed.mjs)\"  first}" "${SERVER_KEY:?}"
RATES=${RATES:-"1000 2000 4000 5000"}
DURATION=${DURATION:-30s}
CONTAINER=${CONTAINER:-bunting-server-1}
OUT=$(mktemp -d)

echo "| scenario | target rps | achieved rps | p50 | p95 | p99 | p99.9 | max | failed | server CPU (avg of 6 cores) | server memory |"
echo "|---|---|---|---|---|---|---|---|---|---|---|"
for scenario in varied etag; do
  for rate in $RATES; do
    [ "$scenario" = etag ] && [ "$rate" != 1000 ] && continue
    ( while true; do docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' "$CONTAINER" 2>/dev/null >> "$OUT/stats-$scenario-$rate.txt" || true; done ) &
    sampler=$!
    k6 run --quiet -e BASE_URL="$BASE_URL" -e API_KEY="$SERVER_KEY" -e SCENARIO=$scenario -e RATE=$rate -e DURATION=$DURATION \
      --summary-export "$OUT/$scenario-$rate.json" load/k6/ofrep-bulk.js >/dev/null 2>&1 || true
    kill $sampler 2>/dev/null || true; wait $sampler 2>/dev/null || true
    node - "$OUT" "$scenario" "$rate" "$DURATION" <<'JS'
const fs = require('fs');
const [dir, scenario, rate, duration] = process.argv.slice(2);
const s = JSON.parse(fs.readFileSync(`${dir}/${scenario}-${rate}.json`, 'utf8')).metrics;
const t = s['http_req_duration{scenario:load}'];
const f = s['http_req_failed{scenario:load}'];
const secs = parseInt(duration);
const reqs = s['http_reqs'].count;
const ms = (v) => (v < 1 ? v.toFixed(2) : v.toFixed(1)) + ' ms';
const stats = fs.readFileSync(`${dir}/stats-${scenario}-${rate}.txt`, 'utf8').trim().split('\n').filter(Boolean)
  .map((l) => l.split(' ')).filter((p) => p.length > 1);
const cpu = stats.length ? (stats.reduce((a, p) => a + parseFloat(p[0]), 0) / stats.length / 6).toFixed(0) + ' %' : 'n/a';
const mem = stats.length ? stats[stats.length - 1][1] : 'n/a';
// the load scenario is rate * duration requests; warm-up adds a fixed 2000
const achieved = ((reqs - 2000) / secs).toFixed(0);
console.log(`| ${scenario} | ${rate} | ${achieved} | ${ms(t.med)} | ${ms(t['p(95)'])} | ${ms(t['p(99)'])} | ${ms(t['p(99.9)'])} | ${ms(t.max)} | ${(f.value * 100).toFixed(2)} % | ${cpu} | ${mem} |`);
JS
  done
done
