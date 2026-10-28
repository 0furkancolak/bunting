# SDK endpoints

All endpoints are under `/api/v1` and authenticated with an API key (`Authorization: Bearer <key>`).

## `GET /sdk/ruleset` (server key)

Returns the whole compilable ruleset of the key's environment as JSON. `ETag` is the ruleset version (`"42"`); send `If-None-Match` to receive `304 Not Modified`. Client keys get `403`.

## `GET /sdk/stream` (server key)

Server-Sent Events. Events:

| Event | Data | Meaning |
|---|---|---|
| `ruleset-updated` | `{"version":42}` | Sent once on connect with the current version, then on each change. |
| `heartbeat` | `{}` | Every 25 s. |

On `ruleset-updated`, fetch `/sdk/ruleset`. Events may be coalesced: only the latest version is guaranteed.

## `GET /client/stream` (client key)

Same behaviour with event `flags-changed`. Browsers may pass the client key as `?apiKey=` because `EventSource` cannot set headers. On `flags-changed`, re-run the OFREP bulk evaluation.
