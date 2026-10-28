# Load test results

Two questions: can the OFREP bulk endpoint serve **1,000 requests/s with p99 < 20 ms**, and can one server hold
**5,000 concurrent SSE connections** and tell all of them about a change quickly?

Short answers: yes and yes, with a lot of headroom. The numbers below are from one laptop, so read them as
"it is not the bottleneck on a small machine", not as a capacity guarantee.

## Setup

| | |
|---|---|
| Machine | Apple M4 (10 cores), macOS 27, 16 GB. Everything below ran on this one machine. |
| Server | the `docker-compose.yml` stack: `bunting-server` image, PostgreSQL 17, in an OrbStack VM (6 CPUs, 2.9 GB). The load generators ran on the host and reached the server through Docker's published port. |
| Tools | [k6](https://k6.io) 2.1 for HTTP, a small Node script ([`load/sse/fanout.mjs`](../load/sse/fanout.mjs)) for SSE. |
| Data | project `load`, environment `production`: 50 flags (boolean rules with segments and negated conditions, percentage rollouts, string/integer/JSON variants, 5 switched off) and 3 segments. See [`load/seed.mjs`](../load/seed.mjs). |

Reproduce:

```bash
docker compose up -d --build
eval "$(node load/seed.mjs)"
load/run-ofrep.sh                                   # OFREP table (rates 1000 2000 4000 5000, 30 s each)
RATES=1000 DURATION=60s load/run-ofrep.sh           # the 1,000 rps / 60 s run
ulimit -n 65536; node load/sse/fanout.mjs --connections 5000 --hold 60 --rounds 5
```

## 1. OFREP bulk evaluation (`POST /ofrep/v1/evaluate/flags`)

Each request evaluates all 50 flags for one context. In the `varied` scenario every request has a different context
(random targeting key out of 1,000,000, plan, country, app version, beta flag), so every request is a full evaluation
and nothing can come from an HTTP cache. In the `etag` scenario 100 users poll repeatedly with `If-None-Match`, as a
browser SDK would; about half of the answers are `304`.

k6 uses a constant *arrival rate*, so a slow server cannot slow the test down: late responses show up as latency.
A 10 s warm-up at 200 rps precedes every run. Each server run started from a freshly restarted container, so the
first row includes JIT warm-up.

| scenario | target rps | achieved rps | p50 | p95 | p99 | p99.9 | max | failed | server CPU (share of VM) | server memory |
|---|---|---|---|---|---|---|---|---|---|---|
| varied | **1000** | 1000 | 0.29 ms | 0.84 ms | **3.1 ms** | 17.3 ms | 43.0 ms | 0.00 % | 7 % | 99 MiB |
| varied | 2000 | 2000 | 0.18 ms | 0.32 ms | 0.74 ms | 2.0 ms | 23.8 ms | 0.00 % | 6 % | 97 MiB |
| varied | 4000 | 4000 | 0.14 ms | 0.40 ms | 0.81 ms | 1.9 ms | 9.7 ms | 0.00 % | 10 % | 105 MiB |
| varied | 5000 | 5000 | 0.13 ms | 0.39 ms | 0.87 ms | 3.7 ms | 29.1 ms | 0.00 % | 12 % | 106 MiB |
| etag | 1000 | 1000 | 0.32 ms | 0.52 ms | 1.4 ms | 2.5 ms | 4.7 ms | 0.00 % | 5 % | 99 MiB |

**Target met:** at 1,000 rps the p99 is 3.1 ms against a 20 ms budget, with zero errors. A separate 60 s run at
1,000 rps on a warmed-up server gave p99 1.4 ms (60,000 requests, none failed).

**Reading the table**

- Latency does not grow with rate up to 5,000 rps; the server uses about a tenth of the VM. The compiled rule engine
  evaluates a flag in under a microsecond ([benchmarks](benchmarks.md)), so 50 flags are noise next to JSON and HTTP.
- The tail (p99.9 and max, up to tens of milliseconds) comes from garbage collection and from the load generator and
  server competing for the same laptop. It is not a property to rely on either way.
- 5,000 rps is the highest rate tried, because the default rate limit is 6,000 requests/s per API key
  (`Bunting:RateLimit`). Where the real ceiling lies was not measured.
- Not measured: a large ruleset (thousands of flags), network latency, TLS, or a database under heavy write load.

## 2. 5,000 concurrent SSE connections (`GET /api/v1/sdk/stream`)

5,000 server-key connections opened at 250 per second, held for 60 seconds (two heartbeat intervals), then a flag
was changed through the Management API five times. "Delivery time" is measured from the moment the change request
is sent until each connection has received the `ruleset-updated` event, so it **includes** the Management API call
itself (39–131 ms, a transaction with audit entry and version bump).

| | result |
|---|---|
| Connections established | 5,000 of 5,000, none failed (20 s ramp) |
| Time to first event | p50 29 ms, p99 103 ms |
| After 60 s | 5,000 still open, 0 dropped; every connection received a heartbeat (11,750 in total) |
| Change delivery, 5 rounds × 5,000 | 25,000 of 25,000 delivered; p50 85 ms, p95 167 ms, p99 171 ms, **max 172 ms** |
| Server resources | 228 MiB after the ramp, 240 MiB while holding, 14 % CPU of the VM while holding (heartbeats) and about 1 % idle |

**Target met:** all connections survived, nothing was lost, and a change reached all 5,000 in well under the
one-second goal of the real-time phase (worst case 172 ms including the write itself).

**Reading the results**

- Each connection costs roughly 25 KiB of server memory (about 140 MiB for 5,000 on top of a 100 MiB baseline).
- The first change after a fresh start was the slowest (158 ms median); later rounds were faster.
- This is one server instance with one client process on the same machine. Several instances behind a load balancer
  share the work; the cross-instance path is covered by the multi-instance tests ([ADR 0011](adr/0011-realtime-propagation.md)).
- Not measured: connection churn (reconnect storms), slow clients, or more than 5,000 connections.
