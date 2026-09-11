# ADR-0011 — Permissions model: JWT identity, Site-scoped roles, server-resolved authorization

**Status:** Accepted
**Date:** 2026-09-11

## Context

Phase 5's test gate — "a non-privileged user cannot write to a tag or
view a site outside their permitted scope" — needs a permissions model,
and `phase-plan.md` deliberately blocks Phase 5 on this ADR rather than
letting it get decided ad hoc inside an implementation PR. Two things
surfaced while checking the actual code during design review, and both
change what this ADR has to cover, not just how.

First, site-scoping cannot be a REST-only concern. Both the tag and alarm
SignalR broadcasters currently push to `Clients.All`, and the hub's
`GetCurrentValues`/`GetCurrentAlarms` return everything unfiltered;
`/api/tags`, `/api/alarms`, and `/history` are the only currently-scoped
candidates. A permissions model that only guards REST endpoints would
pass on paper while every value and every alarm still reaches every
connected browser over the live push channel — the same shape of mistake
as Phase 2's chart fabricating data through a gap: code that looks like
it does the thing the gate checks for, without actually doing it.

Second, the gate's "cannot write to a tag" half currently has nothing to
test. Driver modules implement `WriteAsync`, but nothing in the Gateway
calls it — there is no write path at all yet, permissioned or not.
Building that path is now part of this phase's scope, not a pre-existing
feature this ADR merely gates.

## Decision

**Authentication.** The Gateway issues its own signed token (JWT) after
a username/password login — no external identity provider. ADR-0006
doesn't name one, and the primary on-premises topology (Docker Compose,
no guaranteed internet access) makes a self-contained mechanism the
right default; OIDC/enterprise SSO stays a future ADR if a customer
deployment ever needs it. Passwords are hashed with
`Microsoft.AspNetCore.Identity`'s `PasswordHasher<TUser>`
(PBKDF2-HMAC-SHA256) rather than a third-party algorithm such as
Argon2id — a first-party .NET component with no dependency on the rest
of ASP.NET Core Identity (no EF-backed user store), so it adds no new
external package under ADR-0006, and PBKDF2 through a maintained,
standard implementation is adequate here. Passwords are never stored or
logged in reversible form.

**Token scope and revocation.** The token carries identity only
(`UserId`) — never baked-in roles or Site assignments. Every request
resolves current roles and Site scope from the database, cached and
invalidated the same way `TagCatalog` already reloads on a config change
(Phase 2). Deactivating a user (soft-delete, ADR-0009) or changing their
role takes effect on their very next request, not whenever a long-lived
token happens to expire — for a system that can write to real equipment,
how fast access actually stops matters more than saving one lookup per
request.

**Roles.** Three fixed roles: Admin, Operator, Viewer. Viewer reads
within permitted Sites only; Operator additionally writes tags and
acknowledges alarms within permitted Sites; Admin is unrestricted,
including UDT template management. No custom/configurable roles in
Phase 5 — nothing here forecloses adding them later if a real need
arises.

**Scope granularity.** Operator and Viewer are scoped per Site via a
`user_site_role` table (`UserId`, `SiteId`, `Role`) — a user can hold
different roles on different Sites. Admin is **tenant-wide, not
Site-scoped**: ADR-0010's device templates are tenant-scoped, not
Site-scoped, so a Site-scoped Admin could never cleanly own template
management; and ADR-0004's single-Tenant-per-deployment model means a
tenant-wide Admin already covers "manages this whole installation,"
which is what Admin is for. Admin is a flag on the user, not a row in
`user_site_role`.

**SignalR is in scope, not just REST.** Tag and alarm broadcasts move
from `Clients.All` to a SignalR group per Site; a connection only joins
the groups for Sites its user is currently permitted to see, resolved
and re-checked the same way as any other request.
`GetCurrentValues`/`GetCurrentAlarms` and the `/api/tags`, `/api/alarms`,
and `/history` endpoints all filter by the caller's permitted Sites —
there is no path, push or pull, that bypasses this.

**The write path.** A new endpoint accepts a tag write, checks the
caller holds Operator or Admin on that tag's Site, and calls the owning
driver's `WriteAsync`. This did not exist before Phase 5 and is now part
of its scope — the gate cannot test "cannot write to a tag" against a
write path that isn't there.

**First Admin.** No default credentials ship. When the user table is
empty, the Gateway accepts an initial Admin username/password from a
one-time CLI argument or environment variable at startup, never a seeded
row — shipped default credentials are a known, recurring weakness in
SCADA deployments specifically, and this project does not repeat it.

**Audit trail.** A single generic, append-only `audit_log` table
(`ActorUserId`, `Action`, `EntityType`, `EntityId`, `TimestampUtc`, an
opaque detail payload) records authentication events and every
permission-relevant write, rather than `created_by`/`updated_by` columns
added to every existing table — one new table, the same pattern already
used for `alarm_definition` and `device_template` rather than reshaping
existing ones. The table is append-only at the database level (the
application's database role has no `UPDATE`/`DELETE` grant on it) — the
same "make the wrong thing impossible, not just discouraged" reasoning
as the composite FKs and `deleted_at IS NULL` checks elsewhere.
Acknowledging an alarm now writes a permanent `audit_log` entry naming
the acknowledging user, closing the "who acknowledged" gap Phase 3
deliberately left open — but only that gap: alarm *state* itself remains
in-memory only, which stays the separate, already-flagged item to close
before Phase 6.

Table names avoid the SQL keywords `user` and `role` — `app_user`,
`user_site_role` — to stay unquoted and unsurprising in every query.

## Consequences

Every request pays one cached lookup to resolve current permissions, in
exchange for access changes taking effect immediately rather than at
token expiry — the right trade for a system that can write to physical
equipment. Fixed roles are simple to reason about and review but not
customizable; a future ADR is needed if a customer ever needs
finer-grained or custom roles. Tenant-wide Admin means there is
currently no way to grant "Admin over just one Site" — if that need
arises, it is a new decision, not an extension of this one. SignalR
group membership adds a small amount of connection-management
complexity (joining/leaving groups as a user's Site permissions change)
that a naive `Clients.All` broadcast never had to think about.

## Verified in review by

- A Viewer-role token cannot write to a tag (rejected) and cannot
  acknowledge an alarm.
- A user with no `user_site_role` row for a Site receives no data for
  that Site from `/api/tags`, `/api/alarms`, `/history`,
  `GetCurrentValues`, `GetCurrentAlarms`, or the SignalR push stream —
  checked with an actual open SignalR connection receiving nothing for a
  Site the connected user is not permitted to see, not just a REST call.
- Deactivating a user (or removing their `user_site_role` row) blocks
  their next request without waiting for their existing token to expire.
- The write endpoint calls the correct driver's `WriteAsync` and is
  rejected for a caller without Operator or Admin on that tag's Site.
- No password is ever stored or logged in a reversible form.
- `audit_log` rejects an `UPDATE` or `DELETE` from the application's own
  database role.
- An alarm acknowledgment produces exactly one `audit_log` entry naming
  the acknowledging user.
- Starting the Gateway against an empty `app_user` table creates no
  account except the one explicitly supplied via CLI argument or
  environment variable.
