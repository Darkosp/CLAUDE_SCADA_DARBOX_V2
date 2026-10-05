# CLAUDE.md — working instructions for this repository

This file is for Claude Code sessions working in SCADA_DARBOX. Read it, and
the documents it points to, before doing anything else in this repo.

## Read first, in this order

1. [README.md](README.md) — what this project is, current status
2. [docs/architecture/decisions/](docs/architecture/decisions/README.md) —
   every ADR is binding. Do not act against one. If a task seems to require
   contradicting an ADR, stop and raise it instead of deciding unilaterally
   — an ADR is only changed by a new ADR that explicitly supersedes it.
3. [docs/architecture/phase-0-architecture.md](docs/architecture/phase-0-architecture.md) —
   how the components fit together
4. [docs/roadmap/phase-plan.md](docs/roadmap/phase-plan.md) — current phase,
   its scope, and its test gate

## Current status
**How to read a PR number here.** This repository
(`Darkosp/DEEP_SCADA_DARBOX`) was created on 2026-09-26, with the project's
history pushed into it, so the merge commits in that history carry the
predecessor repository's numbers — #1 to #25 — and the notes below use them.
This repository's own PRs start again at #1, which makes a bare number
ambiguous. Six are marked *(this repository)* where they appear: its #1, the
driver logging, its #2, the test-fixture guard, its #3, `certs.sh` under
`bash`, its #4, `linux-arm64`, its #5, the Journal Note's spacing, and its
#6, the edge assignment that is ADR-0019's first half.
`gh pr list` shows what exists here.



**Phase 0 and Phase 1 are both complete and merged to `main`.** Phase 1
(core skeleton) shipped Core, Persistence.TimescaleDb, the Modbus TCP
driver module, the Gateway (Web API + SignalR), schema migrations via DbUp
(ADR-0007), the Angular web client, a Modbus simulator, and 36 passing
tests. Its test gate is confirmed (see `phase-plan.md`'s Phase 1 status
note) against TimescaleDB and a live simulator, including the case of a
device going offline (Bad quality, not a fabricated value).

**Phase 2 (tag browsing and device management) is complete and merged to
`main`** (PR #3; see `phase-plan.md`'s Phase 2 status note) — a new Device
and Tag created, browsed, and live-scanning entirely through the UI on a
second Site, with a working trend chart, no Gateway restart required. Two
real defects were caught by using the feature in the browser rather than
by reading the code: the trend chart was fabricating a straight line
through a Gateway-downtime gap (fixed — same class of mistake ADR-0003
exists to prevent, just in the chart layer), and a form field named
`tagName` was shadowing `HTMLFormElement.tagName` (also fixed).

ADR-0008 closed the "no ORM" question this phase raised: Dapper for the
Tenant/Site/Device/Tag/Folder config tables, DbUp (ADR-0007) still owning
schema, historian staying on plain Npgsql. The deletion-semantics
question Phase 2 deliberately deferred was closed separately in the
design conversation as ADR-0009 (soft delete via an active-row database
view) before Phase 3 began.

**Phase 3 (Alarms) is complete and merged to `main`** (PR #5; see
`phase-plan.md`'s Phase 3 status note) — a threshold on a live tag
produces an Active alarm, acknowledging it while still out of range
moves it to Acknowledged, and it either disappears (if acknowledged) or
becomes Cleared and stays listed (if not) once the value recovers. A
Device going Bad never clears its alarm — a Bad reading has no value to
compare (ADR-0003). Alarm state did not survive a Gateway restart when this
phase closed, and was flagged then to close before Phase 6 (deployment);
**that gap is closed** — by the alarm journal in Phase 5.5 below (ADR-0013).

**Phase 4 (additional drivers and UDTs) is complete and merged to
`main`** (PR #6; see `phase-plan.md`'s Phase 4 status note) — OPC UA runs
alongside Modbus in one Gateway (ADR-0002's module boundary holds for a
second protocol), and three Devices are instantiated from one UDT
through configuration alone (ADR-0010's live-reference semantics:
editing a template propagates to every instance immediately). MQTT
remains deferred until it gets its own push-capable driver contract and
ADR.

**Phase 5 (users, roles and security) is complete and merged to `main`**
(PR #7; see `phase-plan.md`'s Phase 5 status note) — every endpoint
requires a session except health and login, every Site-scoped path
filters by the caller's permitted Sites, both SignalR broadcasters push
per Site instead of to every connection, and a tag-write path exists and
is Operator-gated. ADR-0011 (opaque server-side session token,
Site-scoped Operator/Viewer, tenant-wide Admin, append-only `audit_log`)
and ADR-0012 (migrations run outside the Gateway, which refuses to start
unless the schema matches its build exactly) are both implemented. Every
criterion the Phase 5 gate turns on has a test; `phase-plan.md`'s Phase 5
note names the rows that don't and why.

Two consequences to know before working on this code. **The Gateway no
longer migrates anything** — run `src/Migrator` first, or the Gateway
refuses to start and names both schema versions; `docs/roadmap/phase-1-running.md`
has the current sequence. And **a Site the caller cannot see answers 404,
not 403**, everywhere — an id that exists elsewhere is indistinguishable
from one that does not exist. Don't "improve" that into a clearer 403;
it exists so Site-scoped paths cannot be used to enumerate what a user
may not know about, and there is a test that fails if it changes.

**Phase 5.5 (alarm journal) is complete and merged to `main`** (PR #9;
see `phase-plan.md`'s Phase 5.5 status note). ADR-0013 is implemented:
alarms persist as an append-only `alarm_event` journal that is the source
of truth, the live list is rebuilt from it at startup, evaluation
start/stop is journalled so an outage reads as an outage, and shelving
has a required, capped expiry chosen from a fixed list. A Journal screen
reads the history, Site-filtered — except engine events, which belong to
no Site and are shown to every reader, because a Viewer on one Site
still has to know the system was not watching. **Alarm state now
survives a Gateway restart.**

The gate was walked by hand as well as tested, and the hand walk found
ten defects the suite had passed (listed in the status note). Treat that
as the working rule rather than an anecdote: a phase that has a screen is
not done until someone has used the screen. Two things were deliberately
deferred and are recorded under Phase 5.5 in `phase-plan.md`: filtering
the journal, and flapping (deadband / on-delay), the latter needing an
ADR before any code.

**Phase 6 (on-premises deployment packaging) is complete and merged to
`main`** (PRs #10–#17; see `phase-plan.md`'s Phase 6 status note). The
phase was split when it began — the cloud topology became Phase 7,
because it needs ADRs (push-driver contract, broker, store-and-forward,
edge identity) rather than packaging. What exists now: `deploy/`
carries a Compose file, `.env.example`, `build-images.sh` and
**`deploy/README.md`**, the guide an operator follows to install,
upgrade and — tested end to end, not described — roll back. The Gateway
serves the built Angular client from its own origin, so there is no
CORS allowance and no Gateway URL in the client.

Three rules this phase leaves behind. **ADR-0014**: one migrator at a
time, enforced by a database advisory lock, and a build that embeds
zero migration scripts refuses to run — both measured, not assumed.
**Images are built from a commit** (`deploy/build-images.sh` uses `git
archive`), never from the working directory. And **a command that
returns is not a system that is ready**: `docker compose up -d
timescaledb` returns before the database accepts connections, which is
why the restore procedure uses `--wait`; this cost a failed restore
during the write-up.

**Phase 6.5 (names unique within their parent) is complete and merged to
`main`** (PRs #18, #19; ADR-0015). A device, tag or folder name is unique
within its parent among live rows, ignoring case; the API answers 409 with
a message that names what exists and where; and the upgrade renamed a
duplicate that was really there rather than refusing to start, auditing
the rename. The null parent was the trap — under plain `NULLS DISTINCT`
the constraint quietly exempts everything sitting directly under a Site.

One habit this phase leaves behind, worth more than the feature: **after
an upgrade, hard-refresh before judging the client.** A conflict message
appeared in the wrong place for a whole round trip because the browser
still held the pre-upgrade client while the server was new. `index.html`
is served `no-cache` now, but the habit is the real protection.

**Phase 7 (cloud topology) is in progress.** Its ADRs are decided —
ADR-0016 (a driver declares itself polled or pushing, and silence past a
declared staleness limit reads as Bad), ADR-0017 (the link: the edge
acquires and buffers but does not evaluate alarms, the payload is ours
rather than Sparkplug B, Mosquitto over TLS with a certificate per edge,
and a bounded on-disk buffer that records the window it lost) and ADR-0018
(that buffer is SQLite, amending ADR-0006) — and steps 1–5 of the plan are
merged to `main`: the pushing contract (PR #20), `Drivers.Mqtt` (PR #21),
the edge agent (PR #23), cloud ingestion (PR #24), and TLS with per-edge
certificates plus the cloud Compose guide (PR #25), and the logging step
beside them (this repository's PR #1). **The gate itself is walked** — one
machine, with the link cut for real, recorded in
`docs/roadmap/phase-7-manual-gate.md`: 2 min 5 s with the edge off the cloud
stack's network; the readings measured inside the outage stored after the
reconnect with their own timestamps; the 190 readings a lowered bound dropped
reported as one entry with its count and both ends; an outage nothing was
measured in adding no row; and the suite, with a database reachable, at 311
passed and 17 skipped across seven projects — and the one defect the walk found
outside the procedure, `deploy/cloud/certs.sh` unparseable under `bash` and
missing its executable bit, is fixed in this repository's PR #3, merged after the
walk. `linux-arm64`, which step 3
left unverified, is built and run under emulation since 2026-09-27 — the image
is arm64, the agent ran under QEMU and its readings reached the cloud database,
with the numbers in that record's own section; that work is this repository's
PR #4, merged. **The screen half of the gate was walked on 2026-09-27** — the
four steps that need a person, in a browser, and step 5's cut watched from that
screen: the device and its tags created through the API the browser itself
calls, the client's suite run again at 47 passed and 0 failed, and a second
outage with the buffer bound left at its default, 14 min 42 s, whose 877
readings per tag were all measured inside it and stored 56.9 ms after the
reconnect. It found a seventh defect and this one was in the client — a Note
whose parts printed as one word, fixed in this repository's PR #5, merged. What
is still open is **a real arm64 board**: the link between two hosts and the
two-clock case were walked on two machines on 2026-10-02, with the numbers in
`docs/roadmap/phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02`,
so every outage walked before that one was a Docker network disconnect on a
single machine. Both drivers
now name the reason a tag has no value
(ADR-0003's silence). That work found a Modbus read with **no timeout** — a
device that accepts the connection and then stops answering held its scan
loop while the tags already read kept their values, silence that looks like
a live plant; the request is bounded at five seconds now.

**What Phase 7 left open is decided, and both halves are built.** How an
edge's device and tag list reaches it, and how the two sides are kept in
agreement, is
`docs/architecture/decisions/0019-edge-configuration-provisioning.md` — the
cloud is the source of truth and derives each edge's configuration onto the
link the edge already holds. The assignment half is merged (this
repository's PR #6, `fe829f0`). The cloud's half is committed too:
`EdgeConfigurationBuilder` derives each edge's devices from the catalogue,
`EdgeConfigurationPublisher` publishes them retained on
`{prefix}/{edgeId}/config`, `GatewayApp` registers it, the cloud Compose file
turns `EdgeProvisioning` on, and `deploy/cloud/mosquitto/acl` lets
`scada-gateway` write that topic and each edge read only its own. **What has
not happened is the walk** — no run has yet had a real broker and a real edge
accept a derived configuration end to end. Every unfinished item, with what it
waits for, is in `docs/roadmap/open-work.md`.

**Three decisions beside it were closed on 2026-10-02**, in the session that
walked Phase 7's gate on two hosts. Every one is decided, implemented and
verified by mutation, and each has a section in `open-work.md` §2.0 recording
what it has **not** had — a walk on a real link:

- **ADR-0020** — the cloud omits a device with no tags from an edge's
  configuration, rather than deriving one its own payload reader refuses (which
  took the edge's whole configuration down with it).
- **ADR-0021** — an edge reports the devices it has been assigned and cannot
  read, on the declaration it already publishes. `EdgeDriversPayload` is now
  **version 2 — the project's first payload version bump** — and the cloud reads
  versions 1 and 2, because the cloud is upgraded first and a version 1 message
  must keep working.
- **ADR-0022** — an edge's link device is derived from the edge and the
  deployment, is **not overridable**, and the two settings that can vary move
  onto the edge (`link_staleness_seconds`, `link_session_expiry_hours`). An
  operator no longer creates a link device, and the API ignores a
  `linkDeviceId` it is sent.

**A fourth was closed on 2026-10-03**, the most consequential of them:

- **ADR-0023** — a tag write to a device an edge reads is **routed to that edge**
  over the link, and is **never retained, never buffered, and never reported as
  done before it is**: five seconds or a 504 saying "not confirmed". A reply is
  matched to its call by a `writeId`, which is the whole mechanism.
  `EdgeProvisioning:WritesEnabled` (on by default) lets a deployment refuse
  writes over the link outright. **This is the first thing in the project that
  lets the cloud change a plant**, so read decision 3's three guardrails in the
  ADR before relaxing any of them — a late sample is still true of its moment,
  and a late command is a request to change a plant after the reason for it has
  passed.

**The suite is green and its baseline is `open-work.md` §2.4–§2.5: 540 .NET
across seven projects with 0 skipped, and the client's 93.** It was *not* green
earlier on 2026-10-02 — §2.5 diagnoses the two load-induced flakes and separates
the one that is a proven race from the one that is read off the failure. One more
flake was seen on 2026-10-03 and recorded there as a **sighting, not a diagnosis**:
its name was not captured, and the project passed twice afterwards.

**What 2026-10-03 left is a walk, not a decision.** `open-work.md` §2.0 records
four things written, implemented and tested that have never been through a real
link: ADR-0020's omission, ADR-0021's reporting and its payload version,
ADR-0022's derived link, and ADR-0023's write. **The write is the one to walk
first**, because it is the only one where being wrong means a plant was changed
or an operator was told it was. ADR-0023's own entry lists what its tests *do*
cover — the cloud's half, the wire format, the edge's half against a real Modbus
slave, the uplink's handling of a message off a real broker, the retain rule and
the ACL against a real broker — so it is not read as "nothing is tested". What no
run has done is cross all of them at once.

**One finding from that work is now closed rather than open.** A device that accepts
a connection and then says nothing was executed on the write path, and it came back
as a failure with a reason after **about twenty seconds**. **That was NModbus's transport
retrying a failed request three times**, which `ModbusTcpDriver` never set — so the
driver's five-second timeout was worth four attempts, and every comment calling the
read bound five seconds was wrong by a factor of four. `Retries = 0` now, measured:
**20.9 s before, 5 s after.** Off rather than tuned because a retried **read** is safe
and a retried **write** is a command sent twice, and a transport that cannot tell them
apart has to take the safe side of both. What is given up is resilience to one
corrupted packet, and the scan loop is the answer there.

**Two defects were closed on 2026-10-05, both of them the kind that get worse as more
is built on them.** The above, and `open-work.md` §2.0c: the demo seeder asked "is
there a tenant yet" and then seeded, so two processes starting at once against an empty
database both seeded — two tenants, two of every row, and every other entity hangs off
a Site. It now takes a transaction-scoped advisory lock before asking.

**Phase 8 is scoped and its first slice is built**: operator screens (HMI),
`phase-plan.md`'s "Later (not yet scoped)" finally given a shape by ADR-0024. A screen
is configuration and not code — two tables, an API, and a client renderer — and **the
decision that shapes the code most is that a component shows its own quality**: there
is no kind that can print a value with nowhere for its quality to go, and a binding the
reader may not see renders as unreadable rather than disappearing, because hiding it
would make a screen look complete while showing less. **What no run has done is put a
screen in front of a person** nor dragged a component, which `open-work.md` §2.0b records
along with what the tests do pin. **An author can build a screen** — add, remove, resize,
retitle, reorder, move between rows, save, delete — through a draft that is sent whole on
save, because a screen is saved by replacing its component set.

## When Phase 1 (or any phase) begins

- Build only what that phase's entry in `docs/roadmap/phase-plan.md` scopes
  — not more. A phase's test gate is the definition of done; don't move to
  the next phase's scope before the current gate is met and confirmed.
- Follow ADR-0006 for every technology choice. Adding a dependency outside
  that table needs a new or amended ADR first, not a unilateral substitution
  because something seemed easier.
- **When a new ADR closes a gap an earlier one recorded as open, amend that
  earlier sentence in the same pull request** — it is a claim about the present
  tense, and left alone it sends a later session looking for finished work.
  The rule and its first instance (ADR-0011 on alarm state, written 2026-09-11,
  false from ADR-0013 on 2026-09-18, corrected 2026-09-27) are in
  `docs/architecture/decisions/README.md`.
- Respect the core/module boundary from ADR-0002: core code never references
  a specific protocol, a domain vocabulary, or an industry regulation.
  Modules are separate projects, composed at compile time — no
  reflection-based dynamic plugin loading. This also means the ASP.NET host
  (Web API, SignalR) and the concrete persistence implementation live
  outside Core, as their own projects — see Solution layout below — so that
  a module never has to reference a hosting or persistence dependency to
  satisfy Core's contracts.
- Tag identity is the stable ID from ADR-0001, never the display path.
  Historian and alarm code must reference tags by ID.
- Every tag value carries the typed union, source timestamp, and quality
  fields from ADR-0003 — don't simplify a driver to always report "Good" or
  drop the source timestamp for convenience. Where a protocol doesn't supply
  a device-side timestamp (e.g. Modbus), use the time of successful read and
  document that explicitly rather than silently treating it as equivalent
  to a real source timestamp.
- Every Site-scoped entity is transitively Tenant-scoped (ADR-0004), even
  though a given deployment has exactly one Tenant row.
- Units are dimension + SI factor (ADR-0005) — never a bare unit string in
  the data model. Raw-value scaling needed to decode a protocol's wire
  format (e.g. a fixed-point Modbus register) is a driver-level concern,
  separate from and prior to a tag's engineering unit.
- Don't add an ORM or another data-access layer without an ADR — ADR-0006
  doesn't name one, so Phase 1 uses plain Npgsql/SQL. ADR-0008 has since
  settled Phase 2's config-CRUD data access: Dapper for the Tenant/Site/
  Device/Tag tables, schema still owned exclusively by DbUp (ADR-0007).
  The historian stays on plain Npgsql — Dapper does not apply there.
  EF Core remains out of scope; don't reintroduce it without a new ADR
  that supersedes ADR-0008.
- Postgres three-valued logic silently defeats filters and constraints
  over nullable columns, in opposite directions: `WHERE site_id = ANY(...)`
  drops every row whose `site_id` is null, while `CHECK (n >= 1)` accepts
  a null `n`, because a CHECK rejects only `false` and a null comparison
  is `unknown`. The second was a real defect, in migration 0009's own
  constraint, caught by a test; the first was spotted while writing
  ADR-0013 and never shipped. Whenever a filter or a constraint touches a
  nullable column, say what happens to null explicitly, and write the
  null case as its own test.
- A guarantee is only proven by watching its test fail. Break it on purpose,
  confirm a **named** test fails — not an incidental one — and keep a control
  that must stay green, so the mutation is shown to have caught the intended
  behaviour and nothing wider. Then restore and confirm the suite is green
  again: a mutation left applied silently poisons every later run.
- **Rebuild after every source change, including after reverting a mutation.**
  `dotnet test --no-build` reports the previous build's result, so a mutation
  looks like it changed nothing, or a restored file looks like it is still
  broken. This has already cost this project one false conclusion. For the same
  reason a skipped test proves nothing: bring the database up before drawing
  any conclusion from a run.
- **A suite that prints "Passed!" can still leave the run failed.** With no
  database reachable, `dotnet test` on the solution exited 1 while all seven
  projects printed `Passed!`. The cause was `TestDatabase`, the class fixture
  eleven test classes take: every test in each of them skips, so
  `InitializeAsync` never runs — and xUnit disposed the fixture anyway, where
  `DisposeAsync` dereferenced the `ApplicationDataSource` it never created.
  Measured on 2026-09-26 while verifying the driver-logging step, on the one
  project: `Failed: 0, Passed: 4, Skipped: 57` with exit 1, beside eleven
  `[Test Class Cleanup Failure (…)] System.NullReferenceException` lines. The
  class named in such a line is whichever one VSTest reached first, not the
  only one affected — an earlier note here blamed `SessionStorageTests` alone,
  and the count of eleven is what was measured. VSTest counts no failure and
  still exits 1, so read the exit code and each project's "Test run" block,
  not the "Passed!" line. The guard, and a test that builds the fixture and
  disposes it uninitialized, are merged in this repository's PR #2. With it,
  on `main` at 4bbbae5 with no database reachable, all seven projects exit 0 —
  194 passed, 133 skipped (those need TimescaleDB) — and the client's `npm
  test` runs 47 over the pieces testable without a browser.

## Solution layout

```
src/
  Core/                     tag engine, driver framework contracts, historian
                            abstraction, alarm engine, users/roles/auth — no
                            ASP.NET host, no concrete persistence dependency
  Persistence.TimescaleDb/  concrete historian implementation (plain Npgsql/SQL,
                            hypertable) behind Core's storage abstraction
  Gateway/                  Web API host, SignalR hub, the scan service wiring
                            drivers + tag engine + historian together; runs with
                            the non-privileged database role (ADR-0011) and
                            refuses to start on a schema mismatch (ADR-0012)
  Migrator/                 one-shot privileged step that applies the DbUp
                            scripts (ADR-0012) — the only component holding the
                            privileged connection string; runs to completion
                            before the Gateway starts (Phase 5)
  Modules/
    Drivers.Modbus/          Modbus TCP driver (Phase 1)
    Drivers.OpcUa/           (Phase 4)
    Drivers.Mqtt/            (Phase 7; Phase 4 deferred it)
  EdgeAgent/                edge process for the cloud topology (Phase 7):
                            acquisition, an on-disk SQLite buffer, and the
                            uplink. Self-contained on the ordinary runtime,
                            **not** Native AOT and not trimmed — ADR-0018,
                            because the OPC UA stack builds objects by
                            reflection and fails under AOT before it reaches
                            the network
  Web/                      Angular application
tools/
  ModbusSimulator/          simulated device used by the Phase 1 test gate
tests/
docs/                       (already established — architecture, decisions, roadmap)
```

Gateway and Persistence.TimescaleDb are separate from Core by design: if
Core hosted the ASP.NET/SignalR layer or a concrete database dependency,
every module would have to reference that just to satisfy Core's contracts
— the reverse of ADR-0002's rule that a module depends only on Core's public
contracts. Adjust this layout only with a reason tied back to an ADR; don't
restructure for taste.

## Open questions you will hit

`docs/architecture/phase-0-architecture.md` lists decisions not yet made
(auth mechanism, HMI editor details, alarm notification channels, any
compliance module). If a task needs one of these resolved, raise it in the
design conversation rather than deciding it inside an implementation PR.

## Working relationship

This project is designed collaboratively (concept, architecture, planning)
in conversation, then implemented here. That loop continues for the life of
the project — this file and the docs it points to will be updated as new
ADRs are added or the phase plan changes. Re-read them if it's been a while
since your last session in this repo.

## How design-conversation documents get here

README, ADRs, the architecture docs, `phase-plan.md`, and this file are
written directly to disk in this repository by the Cowork/Claude session
running the design conversation, through a file-sync mechanism — **not
through git**. That session cannot run git itself and cannot commit or
push. When it says a document was "saved to the repo," that only means the
file content on disk is correct — never that it is committed.

Do not assume a document is committed just because Cowork said it was
added or changed. Always check `git status` for uncommitted changes before
acting on that assumption, and commit/push it yourself.

**Rule:** design-conversation documents (README, ADRs, architecture docs,
`phase-plan.md`, this file) are committed directly to `main`, no PR. The
PR/review process applies to implementation code (Phase 1 onward) — open a
PR (draft, if the phase's test gate isn't confirmed yet) rather than
committing application code straight to main.

## Commits

Commit messages should reference the relevant ADR or phase where relevant,
e.g. `docs: add ADR-0007 — <title>` or `feat(phase-1): tag engine schema
(ADR-0001, ADR-0003)`. Keep documentation commits and implementation commits
separate.
