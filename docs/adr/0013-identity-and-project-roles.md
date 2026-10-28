# 13. Identity and project roles live in the server

Status: accepted

## Context

The admin panel must offer sign-in and per-project roles (Owner, Editor, Viewer). The brief says the admin never touches the database and that a Viewer must be unable to change anything in the UI *and* in the API. ADR 0008 deferred Identity to this phase.

## Decision

- **Server owns identity.** ASP.NET Core Identity (`IdentityUserContext`, no roles tables) lives in the server's database next to the flag data. Users are `BuntingUser` (an `IsInstanceAdmin` flag); `ProjectMember(ProjectId, UserId, Role)` grants a role in one project.
- **Bearer tokens, not shared cookies.** `POST /api/v1/auth/login` checks the password (lockout after 5 failures, identical answer for unknown email and wrong password) and returns an Identity bearer token (12 h). The admin panel is a separate app, so it stores that token inside its own encrypted sign-in cookie and sends it to the Management API. Data protection keys are persisted in the database, so tokens are valid on every server instance and survive restarts.
- **One enforcement point.** The `viewer`/`editor`/`owner` policies now use a `ProjectRoleHandler`. Static tokens and instance administrators hold one role for every project; other users hold the role of their membership for the `{projectKey}` route value. Routes without a project admit any signed-in user at viewer level (project listing filters by membership); creating projects, users and other instance-wide actions need the global owner role. The admin panel hides controls by role as a convenience, but every write is checked by the API.
- **Playground.** `POST /projects/{p}/environments/{e}/evaluate` runs the real engine against the current ruleset with a context supplied in the request body (never stored or logged).
- **Bootstrap.** `Bunting:Bootstrap:AdminEmail/AdminPassword` create the first instance administrator on start-up when that email does not exist.
- Static bearer tokens remain for automation and tests.

## Consequences

- The admin app has no database and no privileged credentials: a compromised admin cannot do more than the signed-in user.
- Users cannot be self-registered; an instance administrator creates them and project owners add them to projects.
- Access tokens last 12 hours (the admin cookie 11 h); an expired token sends the user back to the sign-in page. There is no refresh flow yet.
