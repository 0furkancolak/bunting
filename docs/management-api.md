# Management API

Base path `/api/v1`, `Authorization: Bearer <token>`, errors as RFC 9457 `application/problem+json`.
Roles: **Viewer** (read) < **Editor** (flags, configs, segments) < **Owner** (environments, API keys, members).
The token is either a static token from configuration (one role everywhere) or a user token from `POST /auth/login`; users hold a role per project
(see [ADR 0013](adr/0013-identity-and-project-roles.md)). Creating projects and managing users needs an instance administrator.

| Method & path | Role | Notes |
|---|---|---|
| `POST /auth/login` | – | `{email,password}` → bearer token; `GET /auth/me` returns name, admin flag and memberships |
| `GET/POST /users`, `DELETE /users/{id}` | admin | instance administrators only |
| `GET/POST /projects/{p}/members`, `PUT/DELETE …/{userId}` | O | members of a project; audited |
| `POST /projects/{p}/environments/{e}/evaluate` | V | playground: `{flagKey?, context}` → value, variant, reason, rule id per flag |
| `GET/POST /projects`, `GET/PATCH/DELETE /projects/{p}` | V / admin (create) / O | listing shows only the caller's projects; new projects get `development`, `staging`, `production` |
| `GET/POST /projects/{p}/environments`, `GET/PATCH/DELETE …/{e}` | V / O | new environments get a disabled config per flag |
| `GET /projects/{p}/flags?search=&tag=&archived=` | V | includes per-environment enabled state |
| `POST /projects/{p}/flags` | E | creates a disabled config in every environment |
| `GET/PUT/DELETE /projects/{p}/flags/{f}` | V / E | `PUT`/`DELETE` need `If-Match`; delete requires archived |
| `GET/PUT/PATCH /projects/{p}/environments/{e}/flags/{f}` | V / E | `PUT` = full config, `PATCH {enabled}` = kill switch; `If-Match` required |
| `GET/POST /projects/{p}/environments/{e}/segments`, `GET/PUT/DELETE …/{s}` | V / E | `PUT`/`DELETE` need `If-Match` |
| `GET/POST /projects/{p}/environments/{e}/api-keys`, `DELETE …/{id}` | O | `POST {name,type}` returns the plaintext key **once**; listing shows only a 4-character hint; `DELETE` revokes (idempotent) |
| `GET /projects/{p}/audit?entityType=&entityId=&actor=&environment=&from=&to=&before=&limit=` | V | newest first; `nextBefore` is the paging cursor |

Concurrency: reads return `ETag: "<version>"`; writes send it back as `If-Match`. Missing → `428`, stale → `409` with
`currentVersion` in the problem body. The OFREP endpoints are documented in [ADR 0010](adr/0010-ofrep-implementation.md); the ruleset and SSE endpoints arrive with the real-time phase.
