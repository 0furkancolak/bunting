# 14. Distribution: NuGet packages and container images

Status: accepted

## Context

Applications need the SDK from NuGet; operators need the server and admin panel as something they can run with one command.

## Decision

- **NuGet:** `Bunting.Sdk` (the OpenFeature provider) and the two packages it depends on, `Bunting.Evaluation` (the rule engine) and `Bunting.Domain` (rule/targeting types). All three IDs were free on nuget.org when checked. Versions come from the release tag. Packages are deterministic, carry a README, symbols (`.snupkg`) and Source Link (enabled in CI builds, which have a git remote).
- **Server and admin are not NuGet packages.** `Bunting.Server` is an application; it ships as container images (`ghcr.io/<owner>/bunting-server`, `…/bunting-admin`) built from the Dockerfiles in `src/`, and `docker-compose.yml` wires them with PostgreSQL.
- **Release flow:** pushing a tag `vX.Y.Z` builds, tests, packs, pushes the packages to nuget.org (secret `NUGET_API_KEY`), pushes the images to GHCR and attaches the `.nupkg` files to a GitHub release.
- **Containers** run as the non-root `app` user on port 8080 and have a `/health` check. The admin keeps its sign-in key ring on a volume so restarts do not sign users out; the server keeps its keys in the database.

## Consequences

- Three small packages instead of one, because `Bunting.Sdk` references the engine; consumers see only `Bunting.Sdk`.
- Operators get a one-command self-host, but must change the default admin password from `.env` before exposing it.
