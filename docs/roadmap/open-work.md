# Open work — what is unfinished, and what each item waits for

This is a register, not a source of truth. [`README.md`](../../README.md),
[`CLAUDE.md`](../../CLAUDE.md), the
[ADRs](../architecture/decisions/README.md) and
[`phase-plan.md`](phase-plan.md) remain binding; where this file and one of
them disagree, they win and this file is wrong.

It exists because the project has twice paid for the same mistake: a sentence
that stopped being true sent a later session looking for work that was already
done. Every item below names **what it waits for**, so that when the resource
arrives the work resumes without re-reading the whole history.

Started 2026-10-01. It was written after reading every document in the
repository against the code they describe; the correction that prompted it is
in §2.1.

## 1. Waits for hardware, or for a second machine

None of these can be finished on one machine, and none of them is a defect:
each is a claim the project makes that has only ever been tested under
conditions weaker than the claim.

**§1.1 and §1.2 were walked on two hosts on 2026-10-02** and are no longer
open. The record, with every number and the two things that looked wrong, is
[`phase-7-manual-gate.md`](phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02).
Each item below keeps its own text and carries what the walk changed. §1.3 is
untouched: both hosts that walk used were x64.

### 1.1 A link between two hosts

- **What has never happened.** Every outage walked so far was a Docker network
  disconnect on one machine: the edge container and the cloud stack on the same
  host. No walk has crossed a switch, a firewall or a NAT, and no TLS session
  has been carried over a real wire.
- **Why it matters.** A disconnect is an instantaneous, total, orderly loss. A
  real link fails slowly, partially and asymmetrically — a half-open socket, a
  NAT that drops the mapping, a switch that comes back with a different path.
  The broker session and the edge's reconnect loop are exactly the parts those
  differ on.
- **Needs:** a second machine (or a second host on a real network).
- **Detail:** [`phase-7-manual-gate.md`](phase-7-manual-gate.md) step 0 (the two
  decisions to make before starting), its *What was not measured*, and — added
  2026-10-02 —
  [*Appendix: the walk on two hosts, and two clocks*](phase-7-manual-gate.md#appendix-the-walk-on-two-hosts-and-two-clocks),
  which is the recipe: the broker's name and not its address (`certs.sh` writes
  DNS SANs, so the second machine needs a hosts-file entry), the port proved
  reachable from it first, both clocks right before the skew is staged, and the
  cut made by disabling that machine's network rather than a Docker disconnect.
- **When it exists:** walk that procedure on two hosts, and record it as the
  first walk's record does — the numbers, the cut and reconnect times, and
  everything that looked wrong or merely confusing.
- **Walked 2026-10-02, on two hosts, and closed.** The outage was a real network
  boundary: B's WiFi adapter disabled for 2 min 2 s, not a Docker disconnect.
  `tag_sample` held **119 readings measured inside the outage**, all 379 rows with
  379 distinct times, **0** of them invented for the window, stored as one batch
  at `17:03:01.860266`, the longest having waited **2 min 6.473 s**. The two ends
  saw it differently, which is the reason this item existed: the broker logged the
  client gone only at `17:01:14`, as `disconnected: exceeded timeout`, **20 s
  after the adapter was disabled**. The edge did not hang on the dead socket — it
  logged `Cannot reach the broker` once a second and made 76 reconnect attempts in
  122 s. The record is
  [`phase-7-manual-gate.md`](phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02).
  **What it did not do:** put the two hosts on separate networks behind a router
  or a NAT. Both were on one `/24`, so a NAT that drops a *mapping* — the
  appendix's fourth question — still has not been produced by a walk.

### 1.2 Two clocks

- **What has never happened.** Source timestamps have only ever come from one
  machine's clock, so no `SourceClockSkew` entry has ever been produced by a
  walk. The path exists and is tested (`PushedSources:ClockSkewTolerance`,
  30 seconds by default).
- **Why it matters.** ADR-0017's whole position on time is that the edge's
  clock is neither trusted nor overwritten. With one clock, the walk cannot
  tell "the edge's timestamp was carried across untouched" apart from "the
  store's own time happened to be the same".
- **Needs:** two machines whose clocks genuinely disagree.
- **When it exists:** set one clock past the tolerance, confirm the journal
  records the skew, and confirm the stored `source_time` is still the edge's.
  The two-host appendix above carries the trap: both clocks have to be right
  before the offset is staged, or the walk's own numbers mean nothing.
- **Walked 2026-10-02, on two hosts, and closed.** B's clock was put **+45 s**
  past the 30 s tolerance. The Gateway logged `the source's clock is 47 s ahead
  of the Gateway's; journalled. Its samples keep the times it gave them.` and
  `alarm_event` holds one `SourceClockSkew` row with `clock_skew_seconds =
  46.8458066`, `recorded_at` on A's clock (`17:10:07.975926`) and `source_time`
  on B's (`17:10:54.821733`). **136 samples were stored with B's times, 46.8 s
  ahead of the cloud host's own clock, `quality` Good** — the edge's clock
  neither trusted nor corrected, which is the whole claim. B's clock was put back
  and the live lag returned to the `−1.80 s` measured before the skew was staged.
  **The caveat the walk leaves behind:** neither host had a working NTP source
  (`Source: Local CMOS Clock`), so the pre-skew offset was observed rather than
  set, and the standing `ingested_at − source_time` of about −1.8 s is a clock
  offset plus pipeline delay that this walk does not separate. What it does
  establish is that the staged +45 s appeared in full, to within 5 ms.

### 1.3 A real arm64 board

- **What has happened.** The image and its publish are arm64, and the agent has
  run under QEMU on this x64 machine, with its readings reaching the cloud
  database (this repository's PR #4; numbers in the gate record).
- **Why it matters.** QEMU says the build and the image are right. It says
  nothing about `e_sqlite3` on the board, about performance, or about a plant's
  power and disk.
- **Needs:** the board itself — a Raspberry Pi or whatever the plant uses.
- **Detail:** [`0018-edge-agent-runtime-and-buffer.md`](../architecture/decisions/0018-edge-agent-runtime-and-buffer.md)
  and [`phase-7-manual-gate.md`](phase-7-manual-gate.md#since-the-walk-linux-arm64).
- **When it exists:** build on the board, run the agent, confirm readings reach
  the cloud, and record the board and the image digest.

## 2. Built, and walked on 2026-10-01

### 2.0b Phase 8's first slice, written 2026-10-03

**Screens are built and nothing has looked at one.** The storage, the API, the seeder and the
client renderer all exist with tests behind them; what no run has done is put a screen in front of
a person. This is the same split Phase 7's gate used between its terminal half and its screen half,
and it is recorded here for the same reason: the parts a machine can check are checked, and the part
that needs eyes has not happened.

**What the tests do cover** — six Core rules, five storage tests, eleven through the API, and twelve
on the client's resolver. Three things they pin that are worth knowing are pinned:

- **the composite keys**, at the level they are enforced: a component naming a tag on another Site is
  refused by the database, not by the API. Getting there took two keys rather than one, because a
  tag has no Site of its own — its device does — and the migration records why adding a `site_id` to
  `tag` was refused instead (it would mean recreating `tag_active`, and nothing reads a tag's Site);
- **`readable` is decided by the server**, so every renderer gives the same answer and there is one
  place to check. Mutating it to always-true fails the tag-has-gone test and the cross-Site test;
- **the resolver's four cases**, which is where the client's honesty lives: Bad, unreadable, absent,
  and no-tag-at-all are four different sentences and collapsing any two is the failure the client
  tests exist to catch.

**Two defects were found by writing the tests, and both are worth the record.** The migration granted
nothing, so every read worked and every write failed — since 0009 a new table gets SELECT and INSERT
only and has to ask for more, and the failure is invisible to a read path. And `UpdateAsync`
soft-deleted a screen's components before inserting the new set, which collides the moment an author
resends a component that kept its id; soft deletion exists so history can resolve a *name* (ADR-0009)
and a component has no name, so it is a DELETE and an INSERT.

**What has not happened:**

- **A person has not looked at a screen.** Everything about how it reads — whether a Bad tile stands
  out, whether an unreadable one is obvious without being alarming, whether the twelve-column grid is
  enough for a real screen — is unjudged. Phase 5.5's walk found ten defects the suite had passed,
  most of them only visible on screen, and this is the same kind of surface. **This now includes the
  editor**: the operations behind it are tested as pure functions, and nobody has dragged anything.
- **No screen has been through the browser against a live Gateway.** The resolver is tested as a pure
  function, the editor's operations are tested as pure functions, and the Angular build compiles both
  templates; no run has had either in front of a real SignalR stream with a real Site behind it.
- **The editor has no drag and drop**, and does not pretend to: components are added by a picker,
  sized by a pair of buttons and moved by arrows. That is enough to build a screen and it is not what
  an author would choose twice; a builder is the next thing this wants, and it is a client feature
  over the same rows rather than a new model.
- **Nothing warns that editing a screen affects every reader at once.** A screen has no draft version
  and no publish step (ADR-0024 takes no position on one), so an author pressing Save changes what
  every operator on that Site sees, immediately. That is defensible for a screen and it is the kind
  of thing that should be said on the button rather than discovered.

### 2.0c The demo seeder's race — found, and closed on 2026-10-05

**`DemoConfigurationSeeder.SeedIfEmptyAsync` could seed twice, and no longer can.** It asked
`SELECT count(*) FROM tenant` on one connection and then seeded on another, which is a read followed
by a write with nothing between them: two processes starting at once against an empty database both
read zero and both inserted the whole demo dataset — two tenants, two Skopjes, two of every device.

It was found because `GatewayTestHost` starts the app once per test with the tests running in
parallel against one database, which is a much better double-start generator than a deployment is.
That is also why it was recorded as a sighting rather than a production defect at the time: no
deployment in this project has ever started two Gateways against an empty database. What makes it
worth having closed anyway is the shape — a rolling start, or a Kubernetes `Recreate` written as
`RollingUpdate` — and that the same race duplicates the *Site*, which every other entity hangs off.

**The fix is a transaction-scoped advisory lock taken before the question is asked**
(`pg_advisory_xact_lock`, key `0x5343_4144_5F53_4545`), with the probe and the insert inside that one
transaction. The second seeder waits and then asks its question *after* the first has committed, so
the answer is true rather than merely earlier. Transaction-scoped rather than session-scoped for the
reason `MigrationLock` gives: a process that dies mid-seed releases it without anyone cleaning up.

**Proved by mutation.** With the lock removed, `DemoSeederConcurrencyTests` fails with
`23505: duplicate key value violates unique constraint "tenant_pkey"` — which is what two seeders
colliding on fixed ids looks like, and is also why the test asserts that nobody threw rather than
only that the rows came out once. A later ordinary run is asserted in the same test, because
idempotence on an already-seeded database is what an upgrade depends on and xUnit promises no order
between two test methods sharing one fixture.

### 2.0 Written, and not yet walked

Decided, implemented, tested — and waiting only for a run that exercises it. These are not
defects and not open questions: the work exists and nothing has been through it end to end.

- **[ADR-0020](../architecture/decisions/0020-devices-with-no-tags.md) — a tagless device is
  omitted from the derivation.** Decided and implemented 2026-10-02 from §2.1's finding, with its
  own tests. **What has not happened:** no run has had a tagless device assigned to an edge while
  the new derivation was running, so the path the finding describes — assignment accepted,
  derivation omits it, the log line is written, and the first tag's save republishes it to the
  edge — has been tested in unit tests and not watched on a link. The two-host walk is the
  cheapest place to watch it: assign a second device to that edge with no tags, confirm the
  edge's configuration is still accepted whole, then add one tag and confirm the edge picks the
  device up without a second edit.
- **[ADR-0021](../architecture/decisions/0021-edge-reports-what-it-cannot-read.md) — an edge says
  which assigned devices it cannot read, on its own declaration (version 2).** Decided and
  implemented 2026-10-02, with its own tests and the client showing the result. **What has not
  happened:** no run has had an edge lose a driver under a live assignment. The state it is about
  needs a real edge whose build lacks a driver that one of its assigned devices uses — a stripped
  or rolled-back agent image is the cheapest way — after which the declaration should carry the
  device, the audit trail should name it, `/api/edges` should report it, the edge's panel should
  show it, and the assignment should be unchanged. **And the version half wants a walk of its
  own:** this is the project's first payload version bump, so the claim that a version 1 message
  still reads and a version 3 is refused whole has been tested against this build and never
  against two builds of different ages on a real link. **Also not walked:** that the declaration is
  republished when the set changes and not otherwise — the unit tests watch it through a real
  in-process broker, and no walk has watched it over the TLS link.
- **[ADR-0022](../architecture/decisions/0022-derived-link-device.md) — an edge's link device is
  derived, is not overridable, and the edge names its limits.** Decided and implemented
  2026-10-02, with its own tests. **What has not happened:** no run has created an edge against a
  real deployment and watched the derived link device connect. The unit tests cover the derivation
  — the topic, the settings, one device per edge, a second pass writing nothing, an existing link
  kept — and the Gateway's integration tests cover the API creating an edge with its link already
  in place. What no test does is watch the **derived** link actually subscribe and receive, because
  that needs a broker and an edge. The two-host walk is the place: create a new edge there and
  confirm the Gateway is subscribed to `{prefix}/{edge}/samples` before the edge is started.
  **And one thing an upgrade exercises that nothing here does:** an existing deployment's
  hand-made link devices are reused rather than replaced, which is what keeps the broker's session
  — and the queue under it — from being dropped by the change.
- **[ADR-0023](../architecture/decisions/0023-routing-writes-to-an-edge.md) — a tag write is routed
  to the edge that reads the device, and is never queued or retained.** Decided and implemented
  2026-10-03, with its own tests and four mutations recorded. **What has not happened: a write that
  travels the whole way.** Every joint is now tested and no run has crossed all of them at once — a
  browser asking the API, the router publishing, a real edge taking it off a real broker, a real
  device changing, and the result coming back to the operator's screen. That is the walk this owes,
  and it is the one to do first of the four here, because it is the only one where being wrong means
  a plant was changed or an operator was told it was.

  **What the tests now do cover**, so this entry is not read as "nothing is tested":
  - the cloud's half — the router matching a result to the call waiting for it, the API answering
    504 when nothing answers, the journal keeping `written` / `failed` / `unconfirmed` apart;
  - the wire format — a request and a result round-tripping, and every unreadable one refused whole;
  - **the edge's half, against a real Modbus slave on a real port** — a write reaching the right
    register through the configuration's address and the read path's own scale, and each of the four
    ways it can fail coming back with its own reason. Running this found a defect compiling had not:
    the executor returned a *refusal* (the type for a message that could not be read) where it owed a
    *result*, which carries no write id, so the uplink stayed silent and the cloud would have
    reported "not confirmed" about a write the edge knew had failed;
  - **the retain rule over a real broker** — a write the Gateway publishes is not retained, beside a
    configuration from the same publisher that is, so the difference is the write alone;
  - **the ACL, against Mosquitto** — the Gateway may ask any edge and read any edge's answer; an edge
    reads its own requests and not another's; and an edge cannot answer under another's name. Both
    rules were confirmed to fail when broken, which is what makes them tested rather than inspected.
    The confinement test needed a positive control before it meant anything: an ACL refusing
    everything satisfied it just as well, and the first version of that test passed under a mutation
    that removed the rule.
  - **the uplink's own handling of a message off a broker** — a write published onto the link, into
    the running service, out to a real device, and the answer back on the topic the cloud listens
    to. This was the last joint no test crossed and it was covered by compiling, which is the same
    gap the executor's defect hid behind. Restoring that defect now fails a test **here** as well as
    the executor's own, which is the point: the silence it caused was visible from both ends and
    only one of them was being watched.
  - **a device that accepts a connection and then says nothing** — the shape Phase 7's walk found in
    the *read* path, now executed on the write path too. It came back as a failure with a reason,
    which is what ADR-0023 requires, and it took **about twenty seconds**.

  **And that last one is a finding, not a tick.** The executor has a ten-second deadline and
  `ModbusTcpDriver` sets a five-second socket read timeout, and **neither is what stopped it**: the
  failure arrived as a transport error — `Unable to read data from the transport connection` — after
  roughly twenty seconds. NModbus does not honour the cancellation token during a read, so a
  cancelled token does not interrupt one. What the cloud is told is still correct and ADR-0023's
  honesty rule holds, but **the bound this path was believed to have, it does not have.** Nothing
  in the code claims otherwise now; the executor's remarks carry the measurement. Whether it should
  have a real one — a socket timeout that works, or a driver contract that takes a deadline — is a
  decision nobody has made, and it is the reason this entry stays open.


### 2.1 ADR-0019 configuration provisioning, end to end

**This is the item that caused this file to exist.** Four documents said, in the
present tense, that the cloud's half was not built. It was: commits `cf457da`
(2026-09-28) and `487e07d` (2026-09-29) added it, and the prose written the
following morning did not notice. All four were corrected on 2026-10-01 —
`CLAUDE.md`, `HANDOVER.md`, `phase-plan.md`, `deploy/cloud/README.md`, plus the
same claim in `HANDOVER-archive.md` and in ADR-0019's own context.

- **Built — the cloud's half:**
  `src/Gateway/Provisioning/EdgeConfigurationBuilder.cs` derives one edge's
  devices, driver, settings and tag addresses from the running catalogue;
  `EdgeConfigurationPublisher.cs` publishes them retained on
  `{prefix}/{edgeId}/config`, republishes when the content hash changes and not
  otherwise, republishes everything after a reconnect, and empties the topic of
  a deleted edge; `GatewayApp.cs` registers it;
  `src/Modules/Drivers.Mqtt/EdgeConfigurationPayload.cs` is the versioned
  message both sides share.
- **Built — the edge's half:** `EdgeConfigurationSource`/`Consumer` subscribe to
  that topic over the connection the edge already holds, accept a payload whole
  or not at all, keep the last accepted one in the SQLite buffer, and apply a
  newer one by restarting acquisition. `Edge:Devices` is gone from the options,
  from `src/EdgeAgent/appsettings.json`, from the deleted
  `deploy/edge/edge.example.json` and from the edge Compose file.
- **Built — the deployment:** `deploy/cloud/docker-compose.yml` sets
  `EdgeProvisioning__Enabled: "true"`, the broker host and port, and mounts the
  Gateway's certificate; `deploy/cloud/mosquitto/acl` gives `scada-gateway`
  `topic write scada/edge/+/config` and each edge `pattern read
  scada/edge/%u/config`. `src/Gateway/appsettings.json` leaves it **off**, which
  is a developer's Gateway with no broker and not a statement about readiness.
- **Tested, and passing.** `EdgeConfigurationPublishingTests` runs the publisher against a real
  in-process MQTT server and asserts the retained publish, the late subscriber receiving it, no
  republication when nothing changed, and the emptied topic of a deleted edge — measured
  **3 passed, 0 failed**. `EdgeConfigurationPayloadTests` and `EdgeConfigurationTests` cover the
  payload and the assignment rules, also passing. `BrokerConfigurationTests` covers the delivery
  at the broker itself, against `deploy/cloud/mosquitto` in a container, including
  `The_Gateway_publishes_an_edges_configuration_and_that_edge_reads_it` and
  `An_edge_cannot_read_another_edges_configuration`, which is the ACL above: run on its own with
  Docker reachable, **9 passed, 0 skipped** (2026-10-01). In a whole-solution run those nine
  report as skipped, and §2.3 gives the reason.
- **Walked 2026-10-01**, on one machine, and it worked: the Gateway published three revisions of
  the edge's configuration, the edge accepted the last and answered `Connected to device Plant
  PLC.`, and 30 readings — 30 distinct times, every one `pushed` — arrived in the cloud database
  under the cloud's own tag id, the last measured by the edge and stored 0.19 s later. The numbers
  are in
  [`phase-7-manual-gate.md`](phase-7-manual-gate.md#since-the-walk-the-provisioning-delivered-and-read).
  Nothing on the plant machine named a device, an address or a tag id.
- **Found by that walk, and closed 2026-10-02** (this repository's PR #11). A device whose
  `driverKey` no edge driver answered to was accepted by the Gateway, derived, published, and
  refused only by the edge (`Device X needs driver 'y', which this edge agent does not have.`) —
  loud at the plant, silent in the cloud. ADR-0019 had left "whether an edge may be sent a device
  it cannot reach" to implementation; **§8 now decides it**. The edge declares the driver keys its
  own build has, retained on `{TopicPrefix}/{Edge:Name}/drivers` (`EdgeDriversPayload`, version 1,
  edge to cloud); the cloud records them (migration `0014`) and refuses, by name, a device assigned
  to an edge that has declared it does not have that driver — at the save that assigns it, and when
  the driver of an assigned device is edited. An edge that has **not** declared yet is accepted, and
  shown as having declared nothing rather than as having the Gateway's drivers; a declaration that
  arrives without the driver of a device already assigned to it is recorded in the audit trail with
  the device named, and the assignment is left alone.
- **Walked 2026-10-02**, on one machine, with the real cloud stack — real Mosquitto over real TLS, a
  real Gateway, a real edge agent, images built from PR #11 (`7f9692a`). Before the edge ran,
  `declaredDriverKeys` was `null` and a device whose driver the edge does not have (`mqtt`, which
  the cloud registers and no edge does) was **accepted** into it. The edge then declared two drivers
  at `15:36:07.811`, the cloud read them **0.019 s later**, `/api/edges` answered
  `["modbus-tcp","opc-ua"]` where it had answered `null`, the same save was then **refused 400**
  naming the edge and the drivers it has, a `modbus-tcp` device was accepted `201`, and the one
  device that had been assigned before the declaration was named in the log and in one audit row
  with a null actor. The numbers and the exact output are in
  [`phase-7-manual-gate.md`](phase-7-manual-gate.md#since-the-walk-driverkey-declared-by-the-edge-and-refused-by-the-cloud-2026-10-02).
  A walk on **two hosts** is §1.1's item, not this one.
- **Carried onto two hosts on 2026-10-02, and it worked there too.** The two-host walk
  ([the record](phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02)) ran
  this path end to end for real: the edge started with **0 device(s)**, declared
  `["modbus-tcp","opc-ua"]` on the link it holds, accepted revision
  `sha256:a6dd9e23…` **derived by the cloud**, and logged `Connected to device Pump Station PLC`
  — with nothing on that machine naming a device, an address or a tag id. The declaration was
  audited twice (`17:03:01.772016`, and `16:59:02.72734` on first connect), `unreadableDeviceIds`
  empty both times. This was not a deliberate re-walk of §2.1 — the edge's configuration was
  simply how the walk got a device to read — so treat it as confirming evidence, not as a repeat
  of the walk above. **What it did not test:** a device being assigned *while* the edge is
  connected, or a configuration changing under a running edge. Both edges here accepted one
  revision and kept it.
- **Needs:** nothing beyond Docker to walk it again — the gate record's new section is the recipe.
- **Two flakes, seen once each on 2026-10-02, in the same loaded run.** With the whole Gateway test
  project running beside the other six assemblies — the step above added three in-process MQTT
  brokers to that parallel load — `StartupCheckTests.A_journal_ahead_of_the_build_is_refused_too`
  and `EdgeConfigurationPublishingTests.A_change_is_published_retained_to_that_edges_topic` each
  failed once. Each passed when its class ran alone, and the next whole-project run was green at
  **119 passed, 0 skipped**. Recorded rather than chased, for the reason the flake below is: a test
  that fails only under load makes "the suite is green" mean less than it looks. A fix for exactly
  this class — the broker's port taken at the bind rather than twenty seconds later — was opened as
  this repository's **PR #10 and closed without merging** (2026-10-02), so nothing about it is in
  `main` and the flakes above remain: whoever picks this up should decide whether to port that work
  or to say why it was closed.
- **Also found by the walk, 2026-10-02, and decided the same day:
  [ADR-0020](../architecture/decisions/0020-devices-with-no-tags.md).** A device with no tags is
  derived into a configuration the edge refuses whole. A device assigned to an edge before its
  tags existed travelled in the message, and the edge refused all of it (`device '…' has no
  tags`) — the reader is right, since a device with no tags is not a device, but the cloud
  published it without noticing, which is the shape of the finding this step closed. **The
  decision is that the derivation omits such a device and names it in the Gateway's log**, so
  the operator's natural order — create the device, assign it, add its tags — keeps working and
  the device reaches the edge when its first tag is saved. Nothing was lost when the walk met
  it: the edge kept reading the last configuration it accepted. **This is written but not
  walked** — see §2.0.

### 2.2 The database half of the suite, on this machine — **closed 2026-10-01**

- **What used to happen.** A native PostgreSQL 18 service holds port 5432, so
  anything connecting to `localhost:5432` reaches it rather than the container,
  and authenticates as nobody. Every test that needs a live database reported as
  **skipped**, which is the design (a skipped test proves nothing) but meant a
  "green" run on this machine was only the part that needed no database.
- **What closed it.** The test helpers take `SCADA_TEST_DB_PORT` as well as
  `SCADA_TEST_DB_HOST` (`tests/Persistence.Tests/TestDatabase.cs`,
  `tests/Gateway.Tests/Hosting/TestDatabases.cs`), and the development Compose
  file publishes `${SCADA_DB_PORT:-5432}`, so a container can sit on 5433 beside
  the native server without either giving way. **The native service was never
  stopped** — that was the operator's call to make, and not this repository's.
- **Measured the same day, beside the running native server:** 401 passed, 0
  skipped, 0 failed across seven projects (§2.3).

### 2.3 What a run on this machine measured, 2026-10-01

For the reason in §2.2, read the skipped column before the passed one. `dotnet test
ScadaDarbox.slnx`, **no database reachable**, all seven projects exit 0:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| `ScadaDarbox.Core.Tests` | 105 | 0 | 105 |
| `ScadaDarbox.Drivers.Modbus.Tests` | 27 | 0 | 27 |
| `ScadaDarbox.Drivers.OpcUa.Tests` | 13 | 0 | 13 |
| `ScadaDarbox.Drivers.Mqtt.Tests` | 47 | 0 | 47 |
| `ScadaDarbox.EdgeAgent.Tests` | 24 | 0 | 24 |
| `ScadaDarbox.Persistence.Tests` | 5 | 67 | 72 |
| `ScadaDarbox.Gateway.Tests` | 45 | 67 | 112 |
| **all seven** | **266** | **134** | **400** |

**The same suite, the same day, with a database reachable on 5433** — a throwaway container
beside the native PostgreSQL, `SCADA_TEST_DB_PORT=5433` — and nothing skipped at all:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| `ScadaDarbox.Core.Tests` | 105 | 0 | 105 |
| `ScadaDarbox.Drivers.Modbus.Tests` | 27 | 0 | 27 |
| `ScadaDarbox.Drivers.OpcUa.Tests` | 13 | 0 | 13 |
| `ScadaDarbox.Drivers.Mqtt.Tests` | 47 | 0 | 47 |
| `ScadaDarbox.EdgeAgent.Tests` | 24 | 0 | 24 |
| `ScadaDarbox.Persistence.Tests` | 72 | 0 | 72 |
| `ScadaDarbox.Gateway.Tests` | 113 | 0 | 113 |
| **all seven** | **401** | **0** | **401** |

The client's own suite, `npm test` in `src/Web`: **53 passed, 0 failed**. The previously
recorded figures — 194 passed / 133 skipped with no database, and 311 / 17 with one, both on
2026-09-27 — are in `HANDOVER-archive.md`; the counts grew because the provisioning work added
tests, not because tests were lost.

### 2.4 The same suite after ADR-0019 §8, 2026-10-02 — **425 passed, 0 skipped**

This is the baseline to compare against, and it is the first run that includes the tests §8
added (`EdgeDriverDeclarationsTests`, `EdgeDriverRefusalTests`, `EdgeDriversPayloadTests`,
`EdgeAssignmentTests`, `BrokerConfigurationTests`) with a database reachable, so nothing in it
is skipped. `dotnet test ScadaDarbox.slnx` with `SCADA_TEST_DB_PORT=5433`, beside the native
PostgreSQL on 5432, **exit 0**:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| `ScadaDarbox.Core.Tests` | 105 | 0 | 105 |
| `ScadaDarbox.Drivers.Modbus.Tests` | 27 | 0 | 27 |
| `ScadaDarbox.Drivers.OpcUa.Tests` | 13 | 0 | 13 |
| `ScadaDarbox.Drivers.Mqtt.Tests` | 61 | 0 | 61 |
| `ScadaDarbox.EdgeAgent.Tests` | 25 | 0 | 25 |
| `ScadaDarbox.Persistence.Tests` | 75 | 0 | 75 |
| `ScadaDarbox.Gateway.Tests` | 119 | 0 | 119 |
| **all seven** | **425** | **0** | **425** |

The client's suite, `npm test` in `src/Web`: **60 passed, 0 failed, 0 skipped**.

**And the three flakes did not appear.** §2.1's two and §2.3's one were all seen exactly once
under a loaded parallel run. This run was the same shape — seven assemblies at once, the walk's
containers beside them, the whole Gateway project at 119 — and all three passed. That is one
green run, not a fix: none of the three has been made deterministic, and PR #10 (the broker's
port taken at the bind rather than twenty seconds later) is still closed. Treat this as a
baseline that a future red run can be compared against, not as evidence the flakes are gone.

**Corrected the same evening: the 425 was a lucky run, and this is not the baseline to trust.**
Six further whole-solution runs on the same code — three with a change, three on the commit
without it, same machine, the same test database, the same loaded stack beside them — produced
**one or two failures in every one of them**, and never the same pair twice:

| Test | Seen |
|---|---|
| `Persistence.Tests.EmbeddedScriptsTests.The_gateway_check_refuses_a_build_without_scripts_against_a_database_never_migrated` | 5 of 8, failing in `TestDatabase.DropEmptyAsync` with `NpgsqlException … TimeoutException: Timeout during reading attempt` |
| `Persistence.Tests.DuplicateNameMigrationTests.Duplicates_already_in_the_database_are_renamed_audited_and_then_indexed` | 3 of 8, on the runs where the change was not applied |
| `Gateway.Tests.EdgeDriverDeclarationsTests` — three different tests across runs | 6 of 8, `Assert.Single() Failure: The collection was empty` on the audit row |
| `Gateway.Tests.EdgeConfigurationPublishingTests.An_edge_that_is_deleted_has_its_configuration_emptied` | 1 of 8 |
| `EdgeAgent.Tests.ConfigurationLinkTests.A_configuration_the_edge_cannot_read_is_refused_whole_and_changes_nothing` | 1 of 8 — in the one assembly no change touched at all, which is the clearest evidence that this is the load and not the code |

Every one of them passes when its project is run alone: each of the classes above was run in
isolation, three or four times each, and never failed once.

**No claim about the code follows from this, and one claim about the suite does.** The failures
are not caused by the change measured beside them: the same tests fail on `6c373c6` with none of
it applied, and the pair that fails moves between runs. What they show is that **a whole-solution
run on this machine is not currently green**, so "the suite passes" is not evidence anyone can
use until it is — which is the same conclusion §2.1 reaches about its own two flakes, now with a
reproduction rate behind it. The load is the variable that was not there before: seven assemblies
in parallel, the cloud stack, the walk's simulator, and a test database all on one host.

**What to do about it is not decided, and it is not ADR-0020's business.** The candidates are the
three this file already carries: make the deadlines signals rather than wall-clock waits (PR #10's
shape, closed without merging), give the database-heavy tests their own database rather than one
shared one they create and drop under each other, or serialise the assemblies. Whoever picks it up
should start from the reproduction above — and should note that a *skipped* test proves nothing,
so this is not fixed by making the database unreachable.

### 2.5 The flakes, diagnosed and fixed — **closed 2026-10-02**

Two causes, both of them the same mistake in two places: a wall-clock deadline standing in for a
signal. Neither was in the code under test.

**1. A client-side timeout on a server-side operation.** `ServerConnectionString` in both test
helpers (`tests/Persistence.Tests/TestDatabase.cs`, `tests/Gateway.Tests/Hosting/TestDatabases.cs`)
carried `Command Timeout=10`. That connection does `CREATE DATABASE` and `DROP DATABASE … WITH
(FORCE)` and nothing else, and on a loaded server those are slow — seven assemblies at once,
several of them creating and dropping databases, each migration running hundreds of statements.
The **client** gave up while the server was still working, which surfaced as `NpgsqlException …
TimeoutException: Timeout during reading attempt` inside `DropEmptyAsync`. The change is
`Command Timeout=0` on that connection and that connection only: it is an administrative
connection with no user waiting on it, so it has nothing to protect with a deadline. The
`Timeout=3` beside it stays, because connecting fast and failing fast is still right and the
availability probe depends on it. Per-database connection strings are untouched — those carry a
user's query and a deadline is appropriate there. **Honesty about the evidence:** this cause is
*read off the failure*, not proven load-bearing. The exception names a read timeout on a `DROP`,
and the connection it names carried the ten seconds; but when the ten seconds was put back, three
whole-solution runs and six project-only runs were all green, so the failure could not be
reproduced to order. What reproduced was the *rate* — one or two failures in eight consecutive
runs under the load this session's own concurrent commands were adding — and that load is not
something a later reader can summon on demand.

**2. A wait on the wrong step of a chain, in `EdgeDriverDeclarationsTests`.** `EdgeDriverDeclarations`
records the declaration, reloads the catalogue, and *then* appends the audit row. All three tests
waited only for `Catalogue.Declarations.Count == 1` and then asserted on `Audit.Entries`, so the
assertion was racing the write it checked. Each test now waits for the audit row too. This is the
race the whole file's `WaitUntilAsync` exists to avoid, applied to the last step rather than the
first. **This one is a real race and the fix is not a guess:** the failure was
`Assert.Single() Failure: The collection was empty` — the collection being `Audit.Entries`, the
row the test had never waited for — and the code path appends it after the reload the test did wait
for.

**Measured.** Eight whole-solution runs before either change — three of them on a commit with no
change applied — produced one or two failures each, never the same pair twice. **Seven consecutive
whole-solution runs after them were green**, at **429 passed, 0 skipped** across seven projects,
with the client's 60 passed beside them. The count is 425 plus the four ADR-0020 tests.

**And it stayed green.** ADR-0021 added twenty more tests and five more whole-solution runs were
green, at **447 passed, 0 skipped**, with the client's 63. The two flakes above have not been seen
since the fix.

**Measured again 2026-10-03, after ADR-0023 and its edge-side tests: 502 passed, 0 skipped across
seven projects**, with the client's 63. Whole-solution runs produced it as it grew: **465** before the
edge-side tests, which is the baseline above plus ADR-0023's cloud and wire halves (21 payload tests
in the MQTT module and 7 router tests in the Gateway, one existing Gateway test rewritten to assert
the routing instead of the refusal it replaced); then **492** after the edge's half went in (7
executor tests against a real Modbus slave); then **498** after the broker rules (6 more: 4 write-ACL
tests against a real Mosquitto, and 2 for the retain rule); then **501** after the uplink's own
handling of a write message (3 tests, the last joint nothing had crossed); then **502** after a
device that accepts a connection and then says nothing. The intermediate numbers
were undercounted in an earlier draft of this note by six, because it tried to reconcile them by
adding up per-project deltas instead of reading what the runs printed; the printed numbers are these
and the deltas are not offered as corroboration.

**One flake was seen and is recorded rather than dismissed.** `ScadaDarbox.Gateway.Tests` failed once
in a whole-solution run and the failing test's name was not captured before the output scrolled; its
project then passed 143 of 143 on its own and 149 of 149 in the next whole-solution run. Cause is not
established, so this is a sighting and not a diagnosis — the two diagnosed flakes above stay fixed
and a third, unidentified, is possible. A separate race was found and fixed while writing the uplink
tests: a write is not retained, so one published in the moment between the uplink connecting and
subscribing is genuinely gone, and a test that published once was asserting on that race rather than
on the product. It republishes until answered, and the reason is recorded in the test.

**Not claimed:** that no flake remains anywhere, and not that both changes are individually
necessary. What is claimed is that the diagnosed race is fixed, that the timeout was wrong on its
own terms whatever its share of the blame, and that the reproduction rate went from 8 of 8 red to
7 of 7 green on the same machine, the same stack and the same test database. The third candidate
above — one shared database that several classes create and drop under each other — is still theshape of the design, and a deadline that is a wall clock rather than a signal is still how
`WaitUntilAsync` is written.


What this run does **not** say: nothing above needed TimescaleDB, so the
database-enforced guarantees (append-only tables, the unique-name indexes, the
migrator's lock) were **skipped**, not verified. Only a run with a reachable
database proves those.

**Found and fixed the same day: a skip that named the wrong fact.** With Docker reachable
(`docker version` answered `29.8.0`), the Docker-gated tests reported as *"Docker is not
available ('docker version' failed)"* in a whole-solution run while passing nine of nine when
run on their own. The cause was the probe, not the machine: `BrokerFixture.DockerAvailable`
(`tests/Gateway.Tests/Hosting/BrokerFixture.cs`) was a process-wide `Lazy<bool>` that ran
`docker version` with its output redirected and **waited fifteen seconds**; anything slower — a
loaded machine, a daemon just starting, seven test assemblies at once — was recorded as an
absent daemon, and because it was a `Lazy`, that one observation decided every Docker test in
that assembly.

It is classified now rather than guessed: `ToolProbe` reports Available, Absent, TimedOut or
Blocked, `ToolAvailability` keeps only the answers that are facts about the machine and asks
again after a timeout, and every skip message names which of the four happened. The database
probes carry their exception in the same way — which is how the 67 skips in the first table
came to say `28P01: password authentication failed for user "scada"` instead of a sentence that
blamed the machine. With that in place the same solution run reports **401 passed, 0 skipped**.

**One flake, seen once, recorded rather than chased.** In the first full-solution run after the
three pull requests merged, `EdgeConfigurationPublishingTests.A_configuration_that_has_not_changed_is_not_published_again`
failed with `System.TimeoutException: Timed out waiting for the first configuration to be
published` — a twenty-second window — while all seven assemblies ran in parallel beside the walk's
five containers. It then passed three times out of three with that class alone, passed again as
the whole project (113/113), and a repeat of the same full run was green. That test takes a port
by asking the OS for a free one and releasing it before binding (`FreePort()` in that file), which
another process on a loaded machine can win in between, and its wait is a wall-clock deadline
rather than a signal. It matters for the reason the probe fix above does: a test that fails only
under load makes "the suite is green" mean less than it looks.

## 3. Waits for a decision, before any code

An ADR is changed by a new ADR, never edited into a different decision; and a
task that seems to need one of these raised in the design conversation, not
decided inside an implementation pull request.

| Item | Where it is recorded | What it waits for |
|---|---|---|
| Alarm flapping — deadband, on-delay | `phase-plan.md`, "Deferred out of Phase 5.5" | **an ADR first**: each changes what an alarm *is* |
| Filtering the journal (tag, event type, time) | same place | a phase that wants it |
| Routing a tag write to an edge-assigned device | ADR-0019, Consequences | **closed 2026-10-03 by [ADR-0023](../architecture/decisions/0023-routing-writes-to-an-edge.md)** — see §2.0 for what it still owes a walk |
| The Modbus 5 s response bound: driver constant or per-device setting | `phase-plan.md`, Phase 7 note | the design conversation; ADR-0016's pattern points one way |
| Alarm notification channels and escalation policy | `phase-0-architecture.md`, "Explicitly open" | a design decision, then an ADR |
| TimescaleDB continuous aggregates and native compression | ADR-0006 | the legal review ADR-0006 asks for; the code deliberately does not use them |
| Rollback of a schema migration (down-scripts) | ADR-0012, ADR-0014, `phase-plan.md` Phase 6 | **stays forward-only by decision**; the guide's backup-and-restore is the answer |
| Authoring a screen: drag and drop, a live preview while editing | `phase-plan.md`, Phase 8 "What is left for the next slice" | the editor that exists — see §2.0b for what it does and what it does not |
| Writing a tag from a screen | same place | an ADR: it puts the Operator check and the audit entry behind a button instead of a form |
| Scripting (Jint), reporting | `phase-plan.md`, "Later (not yet scoped)" | a phase that creates a concrete need |
| CI — any pipeline at all | not scoped by any phase | a phase that wants it; today every gate rests on a recorded hand walk |

## 4. Environment, not project

Recorded so a later session does not mistake it for a defect in the code.

- **Nothing in history is rewritten on purpose.** Two commits carry Macedonian
  messages (`487e07d`, `fc95f04`) and one carries the message `msg` under 1,491
  lines of real work (`cf457da`). They are on `main` and pushed; rewriting
  pushed history to tidy a message would cost more than the blemish. Leave them.
- **`report_deepseek.md`** is a generated repository report, untracked until
  2026-10-01 and now named in `.gitignore`. It duplicates documents that live
  here, in an encoding that is not ours; the originals are the ones to read.
- **Docker is reachable in more than one way, and they disagree.** `docker` on
  the Windows PATH resolves to Rancher Desktop, `wsl docker` is refused, and a
  sandboxed process cannot open the Docker pipe at all — so
  `RequiresDockerFact`/`RequiresDockerComposeFact` tests report as skipped from a
  sandboxed run. That is an environment fact about the machine, not about the
  tests: with the daemon reachable they run, as the Phase 7 suite record shows.
- **Line endings.** Committed blobs are LF and the working tree is CRLF. Use
  Windows git; strip the CRs before running a script through WSL's `bash`
  (`tr -d '\r' < script.sh > /tmp/script.sh`), which `deploy/build-images.sh`
  line 27 needs on a fresh checkout.
- **`deploy/.env` and `deploy/cloud/.env` exist locally and are correctly
  ignored.** The cloud stack was once started from a second copy of the
  repository inside WSL, so a running container does not necessarily reflect the
  working tree being read.
- **`OPENSSL_CONF` on this machine points at a file that does not exist**, left by
  another installation: `C:\Program Files\PostgreSQL\psqlODBC\etc\openssl.cnf`. Every
  `openssl` invocation therefore dies before it starts, which cost the 2026-10-01 walk its
  first attempt. **`deploy/cloud/certs.sh` no longer hides it**: it asks `openssl version`
  before anything else and prints openssl's own words when that fails, instead of sending
  each step's reason to `/dev/null` and exiting in silence. Either way, clear the variable
  (`Remove-Item Env:\OPENSSL_CONF`) before making certificates on this machine.

## 5. Documentation that described an older tree

Corrected on 2026-10-01. Each was a present-tense fact that had moved on, and they are listed
here so the same drift is caught faster next time. `HANDOVER.md`'s structural inventory is the
part of this repository most likely to go stale: it describes the tree rather than recording a
decision, and the decisions have ADRs to keep them honest while the tree has nothing.

| Where | Said | Actually |
|---|---|---|
| `HANDOVER.md`, the tree | migrations `0001…0011` | `0001…0013` — 0012 edge assignment, 0013 edge link device |
| `HANDOVER.md`, the tree | `ADR-0001…0018` | `ADR-0001…0019` |
| `HANDOVER.md`, `tests/` | "the table in §1" | the suite table is in `HANDOVER-archive.md`, not §1 |
| `HANDOVER.md`, limitations | the edge's list is copied by hand, and the answer is not built | both false — `Edge:Devices` is gone and the answer is built |
| `HANDOVER.md`, limitations | "133 of 327 .NET tests … skipped" | 134 of 394, measured 2026-10-01 (§2.3) |
| `CLAUDE.md`, solution layout | `Drivers.Mqtt/ (Phase 4)` | Phase 7 — Phase 4 deferred MQTT, as the same file says |
| `phase-7-manual-gate.md`, step 9 | lower the buffer bound in the file `SCADA_EDGE_CONFIG` names | that file and variable are gone; the bound is a configuration key |
| `HANDOVER.md`, trap 1 | the test projects hard-code 5432, so free the port | `SCADA_TEST_DB_PORT` and `${SCADA_DB_PORT:-5432}`; 401 passed, 0 skipped beside the native server |
| `HANDOVER.md`, trap 2 | there is no `docker` CLI on the Windows PATH | it is there (Rancher Desktop, 29.8.0); `wsl docker` is the one refused |
| `HANDOVER.md`, trap 3 | "the repository has no `.gitattributes`" | a root `.gitattributes` was added on 2026-09-27 (`527654a`) |
