# ADR-0011 — Permissions model: opaque session token, Site-scoped roles, server-resolved authorization

**Status:** Accepted
**Date:** 2026-09-11 (token mechanism revised 2026-09-11, before any
implementation existed — see "Revised during pre-implementation review")

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

**Authentication.** The Gateway issues its own **opaque session token**
after a username/password login — a 128-bit cryptographically random
value, stored hashed in a `session` table, never a JWT and never an
external identity provider. ADR-0006 doesn't name an identity provider,
and the primary on-premises topology (Docker Compose, no guaranteed
internet access) makes a self-contained mechanism the right default;
OIDC/enterprise SSO stays a future ADR if a customer deployment ever
needs it.

The token reaches the server as an `Authorization: Bearer` header on
REST calls. It cannot on the SignalR hub: a browser cannot set headers
on a WebSocket handshake, so the SignalR client sends the token as an
`access_token` query parameter. That is accepted **only on the hub
path**, and the Gateway must not log query strings for that path —
a token in a URL is a token that ends up in proxy and server logs.
This is also a second, unplanned argument for the opaque-token decision
recorded below: a leaked URL-borne session token can be revoked the
instant it is noticed, while a leaked JWT stays valid until it expires.

Passwords are hashed with
`Microsoft.AspNetCore.Identity`'s `PasswordHasher<TUser>` rather than a
third-party algorithm such as Argon2id — a first-party .NET component
with no dependency on the rest of ASP.NET Core Identity (no EF-backed
user store), so it adds no new external package under ADR-0006. What is
binding here is **the component**, not a particular set of PBKDF2
parameters: the framework picks and periodically strengthens those, and
`PasswordHasher` versions its own hash format so existing passwords keep
verifying across upgrades. At the time of writing the current .NET
implementation is PBKDF2-HMAC-SHA512 at 100,000 iterations; a future
framework version raising that is an improvement to inherit, not a
deviation from this ADR. Passwords are never stored or logged in
reversible form.

Password rules are **length only**: a minimum of 12 characters, with no
composition requirements (no forced mixed case, digits or symbols).
Composition rules reliably produce predictable patterns — a capital at
the front, a digit and an exclamation mark at the end — while length is
what actually costs an attacker work. 12 is the floor, not a
recommendation; nothing stops a deployment from requiring more.

**Token scope and revocation.** The token resolves to identity only
(`UserId`) — it carries no roles or Site assignments of its own. Every
request resolves current roles and Site scope from the database, cached
and invalidated the same way `TagCatalog` already reloads on a config
change (Phase 2); the session lookup rides the same cache, so validating
a token costs nothing beyond the permission resolution that has to
happen anyway. Deactivating a user (soft-delete, ADR-0009) or changing
their role takes effect on their very next request, not whenever a
long-lived token happens to expire — for a system that can write to real
equipment, how fast access actually stops matters more than saving one
lookup per request. Because sessions are server-side rows, logging out
genuinely ends a session and a single stolen token can be revoked
without touching the user's other sessions.

**Sessions expire, on two clocks.** Revocation is not expiry: without a
lifetime, a stolen token stays valid until someone thinks to log out.
Each `session` row carries both a creation time and a last-seen time,
and is invalid once either an **idle timeout (default 12 hours)** or an
**absolute lifetime (default 7 days)** has passed, whichever comes
first. Idle time is measured against any authenticated use, and an open
hub connection counts as use — an operator watching a screen through a
shift must not be logged out mid-shift, because security that fights the
job it protects gets switched off. Twelve hours covers a long shift plus
handover; seven days forces re-authentication about weekly on a system
that can write to equipment. Both values are configuration, not
architecture: a deployment can tighten them without a new ADR. An
expired session is rejected and its token can never be revived — a new
login issues a new one.

Expiry and revocation are enforced differently, deliberately. Revocation
— a removed role, a deactivated user, a logout — takes effect
immediately, including on an already-open hub connection. Expiry is
swept: a session past either limit is refused for new requests at once,
while an already-open live connection is dropped on the next sweep
(default every 30 seconds) once it passes its **absolute** lifetime. The
idle timeout never ends a live connection at all, because each sweep
counts that connection as use — which follows directly from the rule
above that an open hub connection counts as use: an operator watching a
screen is not idle, and the absolute lifetime is what eventually ends
their session regardless. The seconds of tail after absolute expiry cost
nothing: revocation is a response to something having gone wrong and has
to be instant, while expiry is hygiene on a session that was legitimate
a moment earlier. The sweep interval is configuration.

**Revised during pre-implementation review (2026-09-11).** This ADR
originally specified a signed JWT. Checking the code before starting
implementation surfaced that `Microsoft.AspNetCore.Authentication.JwtBearer`
is a separate NuGet package (pulling the `Microsoft.IdentityModel.*`
chain), which ADR-0006 would require this ADR to name. Re-reading the
decision above made the better answer obvious: a token that carries no
claims and is resolved server-side on every request gets nothing from
being a JWT — the one thing a JWT buys, stateless claim verification,
is exactly what this model already discards — while costing a
dependency and making logout a client-side fiction without a denylist.
The mechanism was therefore changed to an opaque session token before
any code was written against the original. This is recorded here rather
than in a superseding ADR because no implementation ever relied on the
earlier text and the permissions model is otherwise unchanged; splitting
one coherent model across two documents would cost a future reader more
than it records.

**Roles.** Three fixed roles, with every capability assigned explicitly
so nothing is left to a reader's inference:

- **Viewer** — reads tags, alarms and history within permitted Sites.
  Nothing else.
- **Operator** — everything Viewer can do, plus operational actions
  within permitted Sites: writing a tag value, acknowledging an alarm,
  and shelving an alarm. Shelving sits with acknowledging because both
  are operational responses to a live alarm, not changes to how the
  system is configured.
- **Admin** — unrestricted, and the *only* role that may change
  configuration: Folders, Devices, Tags, UDT templates, alarm
  thresholds, and user/role assignment itself. An Operator cannot add a
  device or move an alarm setpoint; changing what the system watches, or
  at what level it alarms, is an engineering action, not an operating
  one.

No custom or configurable roles in Phase 5 — nothing here forecloses
adding them later if a real need arises.

**Scope granularity.** Operator and Viewer are scoped per Site via a
`user_site_role` table (`UserId`, `SiteId`, `Role`) — a user can hold
different roles on different Sites. Admin is **tenant-wide, not
Site-scoped**: ADR-0010's device templates are tenant-scoped, not
Site-scoped, so a Site-scoped Admin could never cleanly own template
management; and ADR-0004's single-Tenant-per-deployment model means a
tenant-wide Admin already covers "manages this whole installation,"
which is what Admin is for. Admin is a flag on the user, not a row in
`user_site_role`.

**The rule, stated generically.** *Every* path that returns or mutates
Site-scoped data filters by the caller's permitted Sites. That is the
rule; the list below is an inventory of what exists today, not the
definition — an endpoint added later is covered by the rule the moment
it exists, and is never exempt because it postdates this list.

A request for a Site the caller cannot see is answered **404, not 403**.
403 confirms that the thing exists, which turns every Site-scoped path
into a way to enumerate Sites, Devices and Tags the caller is not
permitted to know about. This applies to ids that genuinely exist but
belong to another Site, not only to unknown ones.

As of this ADR the Site-scoped paths needing filtering are:
`/api/tags`, `/api/tags/{tagId}`, `/api/tags/{tagId}/alarms`,
`/api/tags/{tagId}/history`, `/api/alarms`, `/api/devices/{deviceId}`,
`/api/sites`, `/api/sites/{siteId}/tree`, and the hub's
`GetCurrentValues` and `GetCurrentAlarms`. The site tree is the one most
easily overlooked and the most literal reading of the gate — it returns
an entire Site's configuration, so an unfiltered `/api/sites/{siteId}/tree`
*is* "viewing a site outside your permitted scope," whatever the other
endpoints do.

The template endpoints are **not** on that list, because Site-filtering
is meaningless for them: templates belong to the Tenant, not to a Site
(ADR-0010). They are gated by role instead — **Admin only**, for reading
as well as editing, consistent with Viewer and Operator having no
configuration rights at all. A device instantiated from a template is
still visible to whoever can see its Site; the template definition
behind it is not.

`/api/health` is deliberately unauthenticated and returns no
Site-scoped data.

**SignalR is in scope, not just REST.** Tag and alarm broadcasts move
from `Clients.All` to a SignalR group per Site; a connection joins only
the groups for Sites its user is currently permitted to see. Permission
changes must drive group membership on **already-open connections**, not
just future ones: the same cache invalidation that makes a revocation
take effect on the next request also removes the affected connections
from that Site's group (or drops them). A live connection that keeps
receiving a Site's values after the permission is revoked is the same
defect as an unfiltered endpoint, arriving through a different door.

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

**That requires splitting the database roles, which this deployment does
not currently do.** Today the application and DbUp both connect as
`scada`, which is a Postgres superuser — and a superuser bypasses every
grant, so an append-only guarantee expressed as "no `UPDATE`/`DELETE`
grant" would be worth exactly nothing while the app connects that way.
A review criterion tested under a superuser connection would pass while
proving nothing, the same shape of false pass as three earlier findings
in this project. So: migrations keep a privileged role, and the
application connects as a **separate non-superuser role** whose grants
on `audit_log` are `INSERT` and `SELECT` only. That role's password
comes from an environment variable, like the first Admin's — never from
a migration script, which is committed and reviewable by design and is
therefore the wrong place for a credential. This means two connection
strings, which Phase 6's deployment packaging has to carry.

This role split is also why migrations no longer run inside the Gateway:
if the serving process held the privileged credential in order to
migrate at startup (ADR-0007), it would hold the key that bypasses this
guarantee, and the guarantee would only cover application bugs rather
than a compromised process. See
[ADR-0012](0012-migrations-run-outside-the-gateway.md), which supersedes
that one clause of ADR-0007 and adds a startup schema-version check so
nothing is lost by the move.

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
complexity (joining/leaving groups as a user's Site permissions change,
including on connections that are already open) that a naive
`Clients.All` broadcast never had to think about. Splitting the database
roles means every environment — a developer's machine included — now
needs two connection strings and a provisioning step that creates the
application role; that is real friction, accepted because an append-only
audit table that a superuser can rewrite is not an audit table.

## Verified in review by

- A Viewer-role token cannot write to a tag, acknowledge an alarm, or
  shelve one; an Operator can do all three within a permitted Site but
  cannot create, edit or delete a Folder, Device, Tag, UDT template or
  alarm threshold.
- A user with no `user_site_role` row for a Site receives no data for
  that Site from *any* listed path — `/api/tags`, `/api/tags/{tagId}`,
  `/api/tags/{tagId}/alarms`, `/api/tags/{tagId}/history`,
  `/api/alarms`, `/api/devices/{deviceId}`, `/api/sites`,
  `/api/sites/{siteId}/tree`, `GetCurrentValues`, `GetCurrentAlarms` —
  with `/api/sites/{siteId}/tree` covered explicitly, since an
  unfiltered site tree alone defeats the gate.
- A Site-scoped request for an id that exists but lies outside the
  caller's permitted Sites returns 404 with the same status and body as
  a request for an id that does not exist at all — the two cases are not
  distinguishable from outside.
- A Viewer or Operator token is refused by both template GET endpoints,
  which are Admin-only rather than Site-filtered.
- Revocation reaches a live connection: with a SignalR connection
  already open and receiving a Site's values, removing that user's
  `user_site_role` row stops the values arriving **on that same
  connection**, without waiting for a reconnect, a new request, or token
  expiry.
- Deactivating a user blocks their next REST request without waiting for
  their existing token to expire, and logging out ends that session
  server-side while the user's other sessions keep working.
- A session past its idle timeout is rejected, and so is one past its
  absolute lifetime even if it has been in continuous use; neither
  token works again afterwards.
- A token presented as an `access_token` query parameter is accepted on
  the hub path and rejected everywhere else, and does not appear in the
  Gateway's request logs.
- The write endpoint calls the correct driver's `WriteAsync` and is
  rejected for a caller without Operator or Admin on that tag's Site.
- No password is ever stored or logged in a reversible form, and no
  session token is stored in the `session` table in recoverable form.
- A password shorter than 12 characters is rejected; a 12-character
  password with no digits, symbols or capitals is accepted.
- `audit_log` rejects an `UPDATE` and a `DELETE` **executed over the
  application's own connection** — a test that runs this as a superuser
  proves nothing and does not satisfy this criterion.
- An alarm acknowledgment produces exactly one `audit_log` entry naming
  the acknowledging user.
- Starting the Gateway against an empty `app_user` table creates no
  account except the one explicitly supplied via CLI argument or
  environment variable.
