# Phase Plan

Each phase has an explicit scope and a test gate. A phase is not "done" by
calendar time or by the perceived amount of code written — it's done when
its gate is met. No phase begins before the previous phase's gate is met.

## Phase 0 — Architecture and foundations

**Scope:** the [Phase 0 Architecture](architecture/phase-0-architecture.md)
document, the foundational [ADRs](architecture/decisions/README.md), this
phase plan, and a CLAUDE.md hand-off for implementation. No application code.

**Test gate:** every foundational decision (ADR-0001 through ADR-0006) is
closed and recorded; the Phase 0 architecture document and this plan exist
in the repository.

## Phase 1 — Core skeleton

**Scope:** the smallest possible end-to-end vertical slice that proves the
architecture, not a feature-complete system. A real Tag engine on
PostgreSQL/TimescaleDB (Tenant → Site → Device → Tag, ADR-0001/0003/0004),
one driver module — Modbus TCP, as the simplest path to a working
end-to-end proof against a simulator — the Web API and SignalR push, and a
minimal Angular page that displays one live tag value updating in the
browser. No alarms, no auth, no UDTs, no tag browse tree yet.

**Test gate:** a value from a simulated Modbus device flows
Driver → Tag engine → (historian row + browser update) end-to-end, verified
against a live simulator.

**Status: gate met (2026-09-09).** Verified against TimescaleDB 2.17.2 and
a live Modbus simulator: live browser updates via SignalR, 145 historian
rows each with distinct source/ingestion timestamps, zero continuous
aggregates and compression disabled on the hypertable, and — the gate's
most important check — a device going offline surfaces as Bad quality on
its tags, not a fabricated zero/false. That last check caught a real defect
(see ADR-0003's rationale): the driver was fabricating `NaN`/`false` for
unreadable values, which both broke JSON serialization and defeated the
whole point of a quality field. Fixed by making tag values genuinely
nullable end to end when quality is Bad.

**Constraint:** historian writes use plain TimescaleDB hypertables only —
no continuous aggregates and no native compression. ADR-0006 flags those
specific features as TSL-licensed, pending legal review before the product
relies on them; the Phase 1 test gate does not require them, so Phase 1
does not use them. Revisit only once ADR-0006's licensing question is
resolved.

## Phase 2 — Tag browsing and device management

**Scope:** the hierarchical browse tree UI (ADR-0001), device configuration
through the UI (add/edit a Device and its connection, no code required),
dimensioned unit display (ADR-0005), and a basic historical trend chart for
a tag.

**Test gate:** a new device and its tags can be added and browsed entirely
through the UI, with a working trend chart.

**Status: gate met (2026-09-09), complete and merged to `main` (PR #3).**
Verified live in the browser, not just by test: on a second, empty Site
(Bitola), a new Device (Bitola Pump 1) and a new Tag (Header Pressure,
unit `bar` from the dimensioned-unit picker) were created entirely
through the UI, appeared in the tree immediately, began scanning live at
Good quality with no Gateway restart, and the trend chart collected
history from the moment of creation — derived path
`Bitola/Bitola Pump 1/Header Pressure` displayed correctly. 46 tests pass;
the integration tests' database-unavailable skip path (see below) was
separately confirmed against an unreachable host (5 Skipped, 0 Passed,
reported as `Skipped!`).

Two real defects surfaced by using the feature, not by reading the code:
the trend chart was drawing a straight line through a gap where the
Gateway had been down for a rebuild, inventing readings no device ever
produced — the same class of mistake ADR-0003 exists to prevent (missing
or Bad data must never be presented as if it were a real value), just in
the chart layer instead of the driver layer this time. Fixed by breaking
the line at any gap wider than 4× the median sample interval (with a 5s
floor, so a fast-scanning tag's normal jitter isn't misread as a gap).
Separately,
a form field named `tagName` shadowed the standard
`HTMLFormElement.tagName` property (named form controls become properties
of their `<form>` element), which broke a DOM-walking tool relying on
`element.tagName`; renamed the field. The demo seed dataset was also found
with only one Site, short of ADR-0001's own review criterion ("any test or
demo dataset includes at least two Sites") — fixed in the seeder.

**Implementation notes (no new ADR needed):**

- The browse tree needs a Folder concept (a grouping node above Device/Tag
  with no data-producing behavior of its own). This is already covered by
  ADR-0001's tag hierarchy — a Folder is a display/organizational node the
  existing hierarchy already allows, not a new kind of identity requiring
  its own decision. Config CRUD for it goes through the same Dapper path
  as Tenant/Site/Device/Tag (ADR-0008). Shape: `Folder(Id, SiteId, ParentFolderId,
  Name)` — `SiteId` required (Site is the mandatory root per ADR-0001 §2/§4),
  `ParentFolderId` nullable and self-referential (null = directly under
  Site), no depth limit, matching ADR-0001 §4's "free-form nested folders".
  `Folder` carries `UNIQUE(SiteId, Id)`, and its self-referential FK is the
  composite `(SiteId, ParentFolderId) REFERENCES Folder(SiteId, Id)` — a
  folder must not be nestable under a parent folder from a different Site,
  same reasoning as the Device invariant below, one level up.
- `Device.FolderId` is nullable and purely organizational (ADR-0001 §6:
  folders carry no functional behavior); `Device.SiteId` stays required and
  is the actual tenant/security scope (ADR-0004). A Device with no
  `FolderId` sits directly under its Site, matching Phase 1's flat layout
  unchanged. The invariant that a Device's Folder must belong to the same
  Site as the Device is enforced as a **database-level composite foreign
  key** — `(SiteId, FolderId) REFERENCES Folder(SiteId, Id)` — not an
  application-level check. This is a security boundary (cross-tenant/
  cross-site placement), and a check living only in application or
  repository code is exactly the kind of thing a future code path (bulk
  import, a new endpoint, a repository bug) can silently bypass; the same
  composite-FK pattern applies to Folder's own self-referential FK above.
- Browse tree API is one recursive `GET /api/sites/{siteId}/tree` returning
  the whole Folder/Device/Tag tree for that Site in a single response, not
  lazy per-node children. Phase 2's test gate is deliberately small (one
  new device and its tags), and CLAUDE.md's "build only what the phase
  scopes" applies here too — lazy pagination solves a scale problem this
  phase doesn't have yet. Revisit (add a lazy `children` endpoint alongside,
  not instead of, this one) only if a real deployment's tree size makes the
  eager fetch a demonstrated problem — no new ADR needed for that either,
  since it wouldn't remove or break the existing endpoint.
- Adding a Device through the UI must make its tags live without a Gateway
  restart (TagCatalog reload / hot-add on new-device-saved). This is a
  service lifecycle detail within Phase 2's own scope ("no code required"
  to add a device), not an architectural decision — it doesn't get a
  separate ADR.
- The trend chart is a small hand-written inline SVG line chart, not a
  charting library. ADR-0006 doesn't name one, and adding one would be
  exactly the kind of dependency-outside-the-table CLAUDE.md requires an
  ADR for. Revisit only via a new ADR if a real need (zoom/pan, multiple
  overlaid series) makes a library clearly worth the dependency — the
  Phase 2 gate doesn't need one.

**Resolved before Phase 3 (2026-09-09):** deletion semantics for
Folder/Device/Tag config were deliberately left out of Phase 2's own
scope and closed separately in the design conversation — see ADR-0009.
Soft delete via a `deleted_at` column and an `_active` view per table
(not application-level filtering), a Folder can only be deleted when
empty (no cascade, no implicit reparenting), and deleting a Device
cascades to its owned Tags. Historian rows are unaffected either way —
ADR-0001's decision that history outlives config stands.

## Phase 3 — Alarms

**Scope:** the alarm engine — thresholds, states (active / ack / shelved),
and an alarm banner/summary screen in the web client.

**Test gate:** a simulated out-of-range value produces a visible alarm that
can be acknowledged.

**Status: gate met (2026-09-10), complete and merged to `main` (PR #5).**
Verified against a real simulated out-of-range value, through the API and
in the browser: a threshold on Discharge Pressure (Skopje) produced an
Active alarm, shown in the banner as `HIGH · Skopje/Pump House/Discharge
Pressure · 3.90 bar (≥ 3)`; acknowledging it while still out of range moved
it from the banner into the summary as Acknowledged; it disappeared
entirely once the value recovered. An unacknowledged alarm instead becomes
Cleared and stays listed (see below). 71 tests pass.

Two behaviors verified live are load-bearing, not incidental: a Device
going Bad (offline) does not clear its alarm — a Bad reading has no value
to compare (ADR-0003), so a standing alarm is left untouched rather than
being misread as "back to normal" at the exact moment an operator has the
least information. And an unacknowledged alarm survives its own recovery
(it becomes Cleared but stays visible) rather than disappearing — a
self-resolved excursion that happened while nobody was watching is
exactly the kind of thing that needs to remain visible after the fact.

**Known gap, to close before Phase 6 (deployment):** alarm state
(Active/Acknowledged/Cleared) lives in memory only and does not survive a
Gateway restart. Out of Phase 3's own scope, but not something to leave
unresolved once this is meant to run somewhere real — an alarm journal
needs a decision (what persists, at what granularity, historian table or
separate) before Phase 6's deployment packaging.

**Implementation notes (no new ADR needed):**

- Threshold shape for Phase 3 is a single high/low pair per tag, no
  hysteresis or deadband — the test gate only needs one simulated
  out-of-range value to produce an alarm, and hysteresis can be added
  later without breaking this shape. Same discipline as the Phase 2
  chart (no library) and tree API (eager, not lazy): build only what the
  gate needs.
- Threshold config lives in a new `alarm_definition` table referencing
  `tag`, through the same Dapper/DbUp path as the other config tables
  (ADR-0008), not as columns on `tag` itself — a tag may later need more
  than one alarm condition (HighHigh/High/Low/LowLow), and a separate
  table extends without reshaping `tag`. If `alarm_definition` rows
  become deletable, they follow ADR-0009's soft-delete-via-view pattern
  like the other config entities.
- Acknowledging an alarm in Phase 3 is anonymous — a state transition and
  a timestamp, no actor identity — because Phase 5 (users/roles/auth)
  hasn't started yet. How an ack ties to a specific user is Phase 5's
  decision to make, not something to guess at now.
- Notification channels (email/SMS/push) are explicitly out of scope.
  Phase 3's own scope is limited to thresholds, state, and an in-app
  banner/summary screen; `phase-0-architecture.md` already lists alarm
  notification channels as an open question for later, not something
  this phase's test gate requires.

## Phase 4 — Additional drivers and UDTs

**Scope:** at least one more driver module (OPC UA and/or MQTT), and device
templates (UDTs) — a device type defined once, instantiated many times.

**Test gate:** three devices are instantiated from one UDT through
configuration alone, no code changes.

**Status: gate met (2026-09-11), complete and merged to `main` (PR #6).**
Verified two ways: three Devices instantiated from one template via the
API, each at a distinct address with a distinct live value; and a fourth
instantiated entirely through the browser, resolving
`holding:10?scale=0.01` from its offset parameter and scanning at
4.45 bar. OPC UA runs against a real in-process `StandardServer`, not a
mock — 6 driver tests, the most important asserting that a value's
source timestamp comes from the server, not the driver's clock (proven
by giving the driver a clock fixed to the year 2000, so a substituted
timestamp could never pass by coincidence). Both drivers — Modbus and
OPC UA — scan side by side in one Gateway, every tag Good: ADR-0002's
module boundary holds for two protocols at once, not just one. 90 tests
pass in total (13 new for the UDT half — 7 address-resolution, 6
ADR-0010 integration tests — plus 6 new OPC UA driver tests).

Three real defects surfaced by the work, not by inspection:
`NodeId.Parse` throws `ArgumentException` for an address it can't parse,
which the driver's catch didn't cover — one bad tag address stopped an
entire device's scan. The Modbus driver has guarded against exactly this
since Phase 1's first commit; OPC UA's own guard had a gap of the same
shape, just a different exception type, caught by a test before it
shipped — underscoring exactly why a second protocol was worth adding
now rather than hitting this same gap a third time in a future driver. The first attempt to
demonstrate the UDT gate proved nothing — all three instances were given
offset `0` and the simulator exposed only one register, so all three
silently read the same address; this is the literal scenario ADR-0010's
Consequences section warns about, caught before being mistaken for a
passing gate. And the Device form had no way to configure an OPC UA
device at all — host/port/unitId were hardcoded to Modbus's shape.

Two smaller findings worth keeping: config-row records lost their
default parameter values, because a defaulted parameter let a Dapper
query that forgot a column bind successfully and fail somewhere else
entirely, instead of at the point of the mistake — the same "make the
wrong thing fail loudly, close to the mistake" reasoning as the
composite FKs and `deleted_at IS NULL` checks elsewhere. And migration
0007 had to recreate three views, not one — `alarm_definition_active` is
built on `tag_active`, so changing `tag`'s shape cascades through a
dependency chain that explicit per-column view definitions (ADR-0009's
own choice, over `SELECT *`) make visible instead of hiding.

MQTT remains deferred — it needs the push-capable driver contract noted
below, which does not exist yet.

**Implementation notes:**

- OPC UA first, not MQTT — decided in the design conversation, not just
  an implementation detail. The driver contract today is pull (the
  scanner calls `ReadAsync` on `ScanInterval`); OPC UA fits it directly.
  MQTT/Sparkplug B is push — the broker delivers on the device's own
  schedule, and there is nothing to poll. Forcing MQTT into `ReadAsync`
  would mean buffering the last-received value and replaying it every
  scan, which either re-historizes a value that didn't recur or requires
  the driver to fabricate a timestamp — both conflict with ADR-0003.
  OPC UA proves the module boundary holds for a second protocol without
  reshaping Core; MQTT is deferred until a push-capable driver contract
  gets its own ADR — which the cloud topology's edge agent will need
  anyway (Phase 0 Architecture, outbound MQTT), so one ADR will serve
  both needs instead of two.
- UDT semantics (live-reference type, materialized per-instance tags,
  template edits propagating immediately) are ADR-0010, not an
  implementation detail — this was a real data-model decision, not
  something to decide unilaterally inside the implementation PR.

## Phase 5 — Users, roles and security

**Scope:** token-based authentication, role-based permissions, and an
audit trail, per ADR-0011 (opaque server-side session token with
server-resolved, cached authorization; Site-scoped Operator/Viewer;
tenant-wide Admin; append-only `audit_log`). The permissions-model
blocker is closed — ADR-0011 exists.

Three things found by checking the code during ADR-0011's own review add
to this phase's scope, rather than being pre-existing features the ADR
merely gates:

- **The tag-write path does not exist.** Driver modules implement
  `WriteAsync`, but nothing in the Gateway ever called it, so "a
  non-privileged user cannot write to a tag" had nothing to test
  against. Building that endpoint is part of this phase.
- **Both SignalR broadcasters push to `Clients.All`.** They move to
  per-Site groups, with `GetCurrentValues`/`GetCurrentAlarms` filtered
  too — and permission changes must reach connections that are already
  open, not only future ones. REST-only enforcement would let the gate
  pass on paper while every value still reached every browser.
- **The application connects to Postgres as a superuser**, which
  bypasses every grant and would make an append-only `audit_log`
  unenforceable. The app moves to a separate non-superuser role
  (`INSERT`/`SELECT` on `audit_log` only) while migrations keep the
  privileged one; its password comes from an environment variable, never
  a migration script. This means two connection strings, which **Phase 6
  packaging must carry**.
- **Migrations move out of the Gateway** (ADR-0012, superseding that one
  clause of ADR-0007) — otherwise the serving process would still hold
  the privileged credential that bypasses the guarantee above. A
  migrator CLI/container applies them; the Gateway instead reads DbUp's
  journal at startup and refuses to start unless it matches the scripts
  its build carries exactly — behind *or* ahead.

Sessions also expire on two clocks (ADR-0011): an idle timeout (default
12 hours, with an open hub connection counting as use so an operator is
never logged out mid-shift) and an absolute lifetime (default 7 days).
Revocation alone is not expiry.

**Test gate:** a non-privileged user cannot write to a tag or view a site
outside their permitted scope — checked over both REST and the live
SignalR push, not REST alone, and with the `audit_log` append-only check
executed over the application's own connection rather than a superuser
one (see ADR-0011's review criteria).

**Status: gate met (2026-09-16), complete and merged to `main` (PR #7).**
130 tests pass, none skipped, with the Gateway tests running the real
host against a fresh database prepared by the migrator's own code and
connected as the non-privileged `scada_app` role.

Every criterion the gate itself turns on has a test: the write refusal,
Site scoping over REST and over the live push, revocation reaching an
already-open connection, 404-not-403, one audit entry per
acknowledgement, and the append-only check executed over the
application's own connection. Four rows of ADR-0011's and ADR-0012's
criteria were confirmed by hand or by search when the code was written
rather than by a test, and are named here rather than rounded off: that
a password is never stored or logged in reversible form, that nothing in
the Gateway calls DbUp's upgrade path, and that the migrator is a no-op
on a second run — all three now have tests (PR #8) — plus that Compose
runs the migrator before the Gateway, which cannot be tested until
Phase 6 builds the Compose files and remains the one outstanding row.

Writing those three tests turned up a real defect that no criterion
asked about: two migrator runs setting the application role's password
at the same time collided (`tuple concurrently updated`), because the
retry waited a fixed interval and every caller woke together. Fixed by
leaving the role alone when it can already log in with that password —
a genuine no-op on a second run — and by jittering the retry for a real
first-time race. The skip is only trusted after a deliberate
wrong-password login is *refused*, so a server configured to accept any
password cannot make the migrator quietly skip setting one.

The verification that matters most here is not the count. Seven
guarantees were broken on purpose, one at a time, and each time the test
that should have caught it did — failing at the assertion that actually
encodes the guarantee, not somewhere incidental: open connections left
in a Site's group after a role was removed, a connection notified but
not moved, the hub token left in the URL, the Operator check removed
from tag writes, a hidden Site answering 403 instead of 404, an
acknowledgement not written to the audit log, and a deleted tag's
history left visible to another Site. A green suite says the code passes
its tests; that exercise says the tests would notice if it stopped.
Revocation on a live connection was additionally checked with a second,
still-permitted connection as a control, so the silence on the revoked
one could not be mistaken for scanning having stopped.

The browser check — run against a scratch database with throwaway
credentials that were dropped afterwards, never the dev `scada` database
— found a real gap that the ADR criteria did not: revoking a Viewer's
role stopped new values reaching their open page, exactly as required,
but the tree and trend already on screen stayed there. Correct by the
letter of the criterion, wrong in a control room, where a frozen view
that still looks live is worse than an empty one. Fixed so that losing
the role clears the tree and trend, and regaining it restores them,
both without a reload.

**Two known limits, deliberate rather than overlooked:**

- Expiry is swept, revocation is immediate. A session past either limit
  is refused for new requests at once; an already-open live connection
  is dropped on the next sweep (default 30 seconds) once it passes its
  **absolute** lifetime. The idle timeout never ends a live connection,
  because each sweep counts that connection as use — which is the
  intended behaviour, not a gap: an operator watching a screen is not
  idle. Revocation, deactivation and logout have no such tail. Recorded
  in ADR-0011.
- Each tag write opens its own short-lived connection to the device
  rather than sharing the scan connection. That keeps a write from
  blocking or disturbing scanning, but cheap Modbus RTUs and small PLCs
  commonly cap concurrent TCP connections at one or two — scanning holds
  one, so a write may be refused, or worse, may cost the scan its
  connection. No device in the test set shows this yet. Revisit when a
  real one does; the alternative (serialising writes through the scan
  loop) trades this for the risk of stalling scans, and is not worth
  building blind.

**Correction (2026-09-18).** "Site scoping over REST and over the live
push" was broader than the tests supported: the live-push test covered
tag values only. Breaking the alarm broadcaster, or the hub's alarm
snapshot, left the whole suite green. Found by mutation-checking during
Phase 5.5, not by reading; the missing test was added there. The
sentence is true now — it was not when it was written.

## Phase 5.5 — Alarm journal

Numbered 5.5 rather than inserted as a new Phase 6 deliberately: several
accepted ADRs and status notes already point at "Phase 6" meaning
deployment packaging, and renumbering would mean editing accepted ADRs
to fix cross-references. A slightly odd number costs less than that.

**Scope:** make alarm state survive a Gateway restart, and give the
system a history of what alarmed, per ADR-0013 — an append-only
`alarm_event` journal as the source of truth, the live list rebuilt from
it at startup, evaluation start/stop recorded so an outage is visible as
an outage, and shelving given a required expiry. A screen to read the
journal (filtered by Site, ADR-0011) is part of this, since a history
nobody can read answers no questions.

**Test gate:** an alarm raised and acknowledged before a Gateway restart
is still listed after it, in the same state and naming the same
acknowledging user; a shelved alarm returns to Active by itself when its
shelf expires; and the journal shows the period during which nothing was
being evaluated.

**Status: gate met (2026-09-22), complete and merged to `main` (PR #9).**
194 .NET tests pass with none skipped, against TimescaleDB and over the
non-privileged `scada_app` connection; the client's `npm test` runs 27
more over the pieces testable without a browser (number parsing, journal
formatting). Sixteen mutations, server and client, each failed its named
test for the right reason and were reverted clean; the results are in
PR #9 rather than in the repository, since a patch bound to exact lines
stops applying with the first edit and then looks like a check that
exists.

The gate was then walked by hand, following
[`phase-5.5-manual-gate.md`](phase-5.5-manual-gate.md), against a live
simulator, a restarted Gateway and a Viewer on a second Site. All three
criteria held on screen: an acknowledged alarm kept its state, its
raise time and its acknowledging user across a restart; a five-minute
shelf ended by itself with an `Unshelved` row carrying no actor; and the
restart read as `EvaluationStopped` / `EvaluationStarted` — for the
Viewer too, who saw the outage and none of the other Site's rows.

Walking it found ten things no test had. One stopped the gate at its
first step: saving an alarm threshold threw `raw.trim is not a
function`, because a number input hands back a number while the draft
was declared as a string — and the fix exposed a quieter defect behind
it, where non-numeric text silently became "no limit". The other nine
were the screen failing an operator while behaving correctly: the banner
showing the value at raise without saying so, two words for one state,
the acknowledging user visible only in the journal, no way to shelve an
alarm once acknowledged, a shelf duration that stayed selected after
use, raw doubles, an outage window without its date across midnight,
the journal stacking above Browse, and internal slugs in the Note
column. All ten were fixed and re-checked on screen before merge. This
is the same lesson as Phase 2's, at larger scale: a green suite said
nothing about any of them.

**Implementation notes:**

- Core gains a persistence dependency it did not have. It stays an
  abstraction in Core with the concrete store in Persistence.TimescaleDb
  (ADR-0002) — the alarm engine must not learn what a database is.
- `alarm_event` is append-only at the database level, like `audit_log`:
  `INSERT`/`SELECT` only for the application role. The test proving it
  runs over the application's own connection; under a superuser it
  proves nothing (the Phase 5 lesson).
- A clear *or a breach* first observed after a restart is recorded with
  the observation time and marked as detected-after-restart. Do not
  write a time the engine never observed — the same rule that stopped
  the driver fabricating values for Bad readings and the chart drawing
  through gaps.
- Migration 0009 must revoke `UPDATE`/`DELETE` on `alarm_event`
  explicitly. Migration 0008 set default privileges so every new table
  is born writable by the application role; ADR-0013 flips that default
  to `SELECT`/`INSERT`, but a table created in the same migration can
  still inherit the old default depending on ordering. The append-only
  test over the application connection is what actually proves it.
- Alarm evaluation currently sits downstream of the historian write in
  `TagEngine.IngestAsync`, so a database failure silently stops alarms
  being evaluated while the scanner logs only "Scan failed". That is
  Phase 1 code, and it is in scope here: this phase's whole promise is
  that the journal shows when nothing was being watched, and today the
  most likely such window is one the journal cannot see.

**Deferred out of Phase 5.5, raised by walking its gate by hand.**

- **Filtering the journal** — by tag, by event type, by time. The screen
  reads the whole window the Gateway returns (newest first, capped at
  1000 rows). On a plant of any size that is not how anyone will look
  for one alarm's history. Server-side, since the Site filter and the
  row limit already are.
- **Flapping.** A value oscillating across a threshold produces a
  complete occurrence each time it crosses: during the gate, one Site
  wrote three journal rows every ~25 seconds. The journal is recording
  faithfully — the alarm really did raise and clear — but a journal that
  fills with an oscillation buries everything else in it, and an
  operator watching the banner learns nothing from it. The remedies are
  standard (a deadband around the limit, an on-delay before raising,
  both in ISA-18.2) and neither is a detail of this phase: each changes
  what an alarm *is*, and so belongs in an ADR before any code.

## Phase 6 — On-premises deployment packaging

Originally scoped as "both topologies". Split when Phase 6 began,
because the cloud half turned out not to be a packaging job: it needs a
push-capable driver contract, a broker, store-and-forward semantics and
an edge identity, none of which are decided. Packaging what exists
should not wait behind decisions that have not been made, so the cloud
topology is now Phase 7 below.

**Scope:** the on-premises topology runs from `docker compose up` on a
machine that has only Docker — Migrator, Gateway (serving the built
Angular client), the database, and the simulators the gate needs.

**Decisions made when this phase was planned:**

- **The Gateway serves the web client.** The built Angular output is
  copied into the Gateway image and served as static files from the same
  origin. That removes the hard-coded `http://localhost:5220` in the
  client, removes the CORS allowance for `localhost:4200`, removes a
  container, and adds no technology to ADR-0006. The client addresses
  the API by relative path.
- **Concurrency of the migrator is ADR-0014**, which answers the
  question this phase carried as open. Two migrator runs meeting is not
  hypothetical: on an existing database a data script applies once per
  run, with every run reporting success, and the Gateway then refuses to
  start for good. It is fixed with a database-level advisory lock, not
  with Compose ordering.
- **Rollback**, which ADR-0012 deferred to this phase, stays
  forward-only: no down-scripts. The deployment guide carries a
  backup-and-restore step taken before the migrator runs.

**Carried in from Phase 5:** the migrator runs to completion before the
Gateway starts (`depends_on: condition: service_completed_successfully`),
and the privileged connection string exists only in the migrator's
environment — the Gateway gets the non-privileged one (ADR-0011,
ADR-0012). Compose takes its secrets from required variables with no
defaults, so a deployment cannot silently run on a password someone
committed.

**Test gate:** on a clean machine, `docker compose up` brings the whole
on-premises stack to a working system: sign in, live values from the
simulators, an alarm raised, the Gateway container restarted with the
alarm still listed in the same state, a simulator stopped and its tags
reading Bad, and `down` followed by `up` keeping both history and alarm
journal. Walked by hand in a browser as well as tested, per the Phase
5.5 lesson.

**Status: gate met (2026-09-23), complete and merged to `main`** (PRs
#10–#17, one per step rather than one stack — a lesson from #11, which
merged into its own base branch instead of `main` and left step 2 off
`main` for four minutes without anyone noticing). 215 .NET tests and 34
client tests pass, none skipped; sixteen-plus mutations, each failing
its named test for the right reason, with the controls green before and
after.

The gate was walked by hand against the real stack, following
[`phase-6-manual-gate.md`](phase-6-manual-gate.md). Everything it asks
for held: the migrator exited before the Gateway started, the Gateway's
environment carried no privileged credential, Compose refused to start
without a secret and named the missing one, both simulators were read
container-to-container by service name, an acknowledged alarm survived
a container restart and a whole `down`/`up` with its raise time and
acknowledging user intact, a stopped simulator read Bad with a real gap
in the trend rather than a line drawn across it, the OPC UA certificate
kept its thumbprint across `--force-recreate`, and a second `up`
changed nothing.

Four things the hand walk found that no test had. The client opened on
a Site with no devices, so the first screen a new user saw was empty.
The alarm summary ignored the Site selector and had no Site column,
so the tree on the left and the table below it disagreed. Two
DataProtection warnings in the Gateway log looked like errors in a
deployment log and were not. And `localhost` on Windows resolved to
IPv6 and reached a different local server — `ApplicationWebServer`,
already listening on 8080 — which answered "Access Error: 404" while
Docker had bound the same port without complaint; not a defect in the
Gateway, but a way to lose an hour, so the guide now checks the port
first and uses `127.0.0.1`. All four were fixed and verified on screen.

Writing the deployment guide found a fifth, and the most costly of
them: `docker compose up -d timescaledb` returns as soon as the
container exists, not when the database is ready, so a restore run
straight after it fails and the migrator then creates an empty
database — the exact sequence someone follows while recovering from an
incident. `deploy/README.md` uses `--wait` and says why. The rollback
path was not described but executed end to end: a probe migration
0010, an upgrade, `pg_restore` back to the earlier dump, the probe
table gone, the older Gateway starting without a schema refusal, and
the same again onto empty volumes as a new machine.

**Implementation notes:**

- **Build the images from a clean checkout, not from the working
  directory.** The point is that an image cannot depend on local state —
  which is exactly what would hide the next item.
- **A case-sensitive build must not embed nothing.** The folder is
  `migrations/` and the csproj embeds it by that name; an earlier draft
  of this note claimed git and the Windows working copy disagreed about
  the case, and that was wrong — the mistake came from a directory
  listing typed with the wrong case, which Windows echoes back happily.
  The risk it pointed at is real all the same, and was then demonstrated
  in a Linux container: built from `Migrations/`, the migrator embeds
  **zero** scripts, exits reporting success, and ADR-0012's schema check
  passes because empty equals empty. The guard ADR-0014 requires — zero
  embedded scripts is a failure, in the migrator and in the Gateway's
  check — is what closes it, together with a test comparing the `.sql`
  files on disk with what the assembly actually carries.
- **The simulators must be reachable from another container.** The
  Modbus simulator binds loopback today; the listen address becomes
  configurable, and Compose sets it explicitly rather than the default
  changing for everyone.
- Prove the Gateway container holds no privileged connection string with
  a check that fails if one is added, not by reading the file once.

## Phase 6.5 — Names unique within their parent

Numbered 6.5 for the same reason 5.5 was: it runs before Phase 7 without
renumbering anything that already points at "Phase 7" meaning the cloud
topology. It is small, and it closes a defect Phase 6 exposed rather than
building something new.

**Scope:** ADR-0015 — a device, tag or folder name is unique within its
parent among rows that are not deleted, case-insensitively; the API
answers 409; an upgrade over a database that already holds duplicates
renames the later rows and writes each rename to `audit_log`. The client
shows the 409 against the name field, and disables Save while a request
is in flight — the second as a courtesy, not as the guarantee.

**Test gate:** the same create request issued twice leaves one device
and a 409, including for a device directly under a Site and for a root
folder, where `NULLS DISTINCT` would otherwise make the constraint do
nothing; a name freed by a soft delete can be used again; and a
migration run against a database seeded with duplicates renames them,
audits each rename, and ends with the index in place.

**Status: gate met (2026-09-24), complete and merged to `main`** (PRs
#18 and #19). 228 .NET tests and 38 client tests pass, none skipped.
The mutations did what they were asked to: without the device index a
second row appears instead of a 409, and with plain `NULLS DISTINCT` a
device directly under a Site and a root folder slip past the constraint
while everything nested still gets its 409 — which is the whole reason
that test exists.

The migration was then run against real data rather than a fixture. The
database already held a duplicate `Pump House`, created by hand while
reproducing the defect; the upgrade renamed the later row to `Pump House
(duplicate e48350d7-…)`, left the original and its tags and alarm alone,
and audited the change. Writing that rename had already found one defect
of its own: an 8-character id suffix produced two identical names,
because seeded ids share their leading characters. The suffix is the
whole id.

Two things the hand walk found that the tests could not. The conflict
message first appeared in the banner at the top of the page rather than
under the Name field — **because the browser was still running the
client from before the upgrade**. `index.html` was served without
`Cache-Control`, so a hard refresh was the difference between seeing the
new screen and judging the old one; it is `no-cache` now, and
`deploy/README.md` says to hard-refresh after an upgrade before
concluding anything about the client. The message also cited
"(ADR-0015)", which means nothing to an operator; it now names what
exists and where — "A device named 'Pump House' already exists directly
under Skopje" — read from the database at the moment of the conflict.

**Implementation notes:**

- The trap is the null parent, and it is the same three-valued-logic
  trap this project has now met twice. A plain unique index over
  `(site_id, folder_id, lower(name))` silently exempts every device
  sitting directly under a Site. PostgreSQL 17 has `NULLS NOT
  DISTINCT`; whatever is used, the test for the null case is what
  proves it.
- Removing the index must make the duplicate test fail by creating a
  second row — the mutation, as usual, is the evidence.
- An idempotency key is deliberately not part of this. It solves a
  different problem (the client cannot distinguish "already created" from
  "refused"), and it belongs to whichever phase first needs machine
  clients rather than people.

## Phase 7 — Cloud topology: edge agent, broker, store-and-forward

**Scope:** the edge-agent-plus-cloud-Gateway split from the Phase 0
architecture, with an edge process that keeps measuring and buffering
while the link is down.

**Decided before any code (2026-09-24), in two ADRs:**

- **ADR-0016** — a driver declares itself polled or pushing, and Core
  treats the two differently by contract. A pushing driver is never
  asked "what is the value now", because it has no honest answer;
  silence past a declared staleness limit reads as Bad, not as
  unchanged. This is the contract Phase 4 deferred rather than deform.
- **ADR-0017** — the link itself. The edge acquires and buffers but does
  not evaluate alarms, so an outage costs alarm coverage and not data,
  and ADR-0013 makes that visible; the payload is ours rather than
  Sparkplug B, carrying exactly ADR-0003's fields; Mosquitto is the
  broker, over TLS with a certificate per edge and a topic prefix it
  may not publish outside of; the buffer is bounded, on disk, survives
  a restart, drops oldest first, and records the window it lost.

**Steps, each a PR of its own, merged as its proof completes:**

1. The pushing contract in Core (ADR-0016), with the staleness rule and
   its mutation. No MQTT yet.
2. `Drivers.Mqtt` as a pushing module against a local broker.
3. The edge agent: acquisition, the on-disk buffer, reconnection —
   self-contained on the CLR, buffering in SQLite (**ADR-0018**, which
   amends ADR-0006). Native AOT was tried and abandoned: the real OPC UA
   module fails under it before it reaches the network, because that
   stack builds objects by reflection. Open with it: `linux-arm64` is
   unverified, and must be built and run before edge hardware is chosen.
4. The cloud side: idempotent ingestion per (tag, source timestamp), the
   dropped-window journal entry, the skew journal entry.
5. Compose for the cloud topology, and the deployment guide beside
   `deploy/README.md`.
6. The hand walk, with the link cut for real.

Beside these, one small step of its own: **Modbus and OPC UA do not log
at all**, while the new MQTT module logs every refused message and every
failed connection with its reason. Worse than the inconsistency: the OPC
UA driver discards the parse error for an unreadable address, so a
mistyped address and an unplugged device look identical on screen — Bad,
with no reason anywhere. That is the silence this project keeps refusing
elsewhere.

**Test gate:** with the edge agent disconnected from the network for a
period and then reconnected, the history contains the samples from the
outage with their original timestamps, and nothing is invented for the
period the link was down.

## Later (not yet scoped)

HMI screen editor and a real component library, the scripting engine
(Jint), reporting. These are deliberately left unscoped until a phase above
creates a concrete need for them, per ADR-0002's module discipline.
