# Admin panel

Blazor Server + MudBlazor. It has no database: everything goes through the server's Management API with the signed-in user's token.

- **Sign in** with the credentials of a server user. The first administrator comes from `Bunting:Bootstrap:AdminEmail/AdminPassword` on the server (the Aspire AppHost sets a local development one).
- **Projects** you are a member of; instance administrators see all and create new ones.
- **Flags**: search, tag filter, enabled state per environment, one-click kill switch (production asks for confirmation). Flag detail: variants, per-environment targeting with ordered rules (drag the handle or use the arrows), condition builder and rollout sliders that must total 100 %.
- **Segments**, **Audit log** (filters, before/after diff), **API keys** (the secret is shown once), **Members** (owners).
- **Playground**: evaluate a JSON context against an environment with the real engine and see value, variant, reason and the matching rule.
- **Roles** (per project): Viewer reads, Editor changes flags and segments, Owner also manages keys and members. Write controls are hidden for lower roles and the API refuses the calls regardless.

Configuration: `Bunting:ServerUrl` (or Aspire service discovery).
