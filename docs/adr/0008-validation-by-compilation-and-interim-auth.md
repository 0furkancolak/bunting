# ADR 0008: Validate with the real compiler; token auth until the admin phase

- Status: Accepted

## Context
Validation rules for flag configs (rollout sums, variant and segment references, regex/semver/date operands, segment
cycles) already exist in `Bunting.Evaluation` because the compiler must neutralise bad content. Re-implementing them in
the server would create two sources of truth.

## Decision
- On every write the server builds a candidate `RulesetSnapshot` and runs `CompiledRuleset.Compile`. Any diagnostic
  (error or warning) rejects the write with an RFC 9457 validation problem. Flag updates are validated against the
  configs of **all** environments; segment changes against their siblings; segment deletes are refused while referenced.
- The Management API authenticates static bearer tokens configured under `Bunting:Management:Tokens`
  (name, token, role). Tokens are compared in constant time. Policies `viewer` < `editor` < `owner` guard every endpoint:
  reads need Viewer; flags, configs and segments need Editor; projects and environments need Owner.
  ASP.NET Core Identity and per-project roles arrive with the admin panel (phase 6) and will feed the same policies.

## Consequences
- Server and SDK can never disagree about what a valid configuration is.
- Warnings that the SDK would tolerate (e.g. an invalid regex) are rejected at write time, which is stricter and safer.
- Roles are global per token until Identity memberships exist (superseded by [ADR 0013](0013-identity-and-project-roles.md)).
