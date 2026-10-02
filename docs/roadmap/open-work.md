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
  decisions to make before starting) and its *What was not measured*.
- **When it exists:** walk that procedure on two hosts, and record it as the
  first walk's record does — the numbers, the cut and reconnect times, and
  everything that looked wrong or merely confusing.

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
- **What that step has not had: a walk.** Everything above is measured — the payload's own tests,
  a declaration arriving over a real in-process broker, the API refusal against a live database, and
  the broker's ACL for the new topic in `BrokerConfigurationTests` — but no run has yet had a real
  Mosquitto, a real Gateway and a real edge agent agree on a declaration end to end, which is the
  same bar §2.1's walk set. **Needs:** nothing beyond Docker; the recipe is §2.1's, with the
  declaration read out of the Gateway's log and the edge's `EdgeDriversPayload` seen arriving.
- **Two flakes, seen once each on 2026-10-02, in the same loaded run.** With the whole Gateway test
  project running beside the other six assemblies — the step above added three in-process MQTT
  brokers to that parallel load — `StartupCheckTests.A_journal_ahead_of_the_build_is_refused_too`
  and `EdgeConfigurationPublishingTests.A_change_is_published_retained_to_that_edges_topic` each
  failed once. Each passed when its class ran alone, and the next whole-project run was green at
  **119 passed, 0 skipped**. Recorded rather than chased, for the reason the flake below is: a test
  that fails only under load makes "the suite is green" mean less than it looks.
- **Needs:** nothing beyond Docker to walk it again — the gate record's new section is the recipe.
  A walk on **two hosts** is §1.1's item, not this one.

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
| Routing a tag write to an edge-assigned device | ADR-0019, Consequences | an ADR — the link is outbound only and the Gateway has no route |
| A link device derived from the edge rather than named | ADR-0019, Consequences | implementation, once decided — it is the last thing an operator types |
| The Modbus 5 s response bound: driver constant or per-device setting | `phase-plan.md`, Phase 7 note | the design conversation; ADR-0016's pattern points one way |
| Alarm notification channels and escalation policy | `phase-0-architecture.md`, "Explicitly open" | a design decision, then an ADR |
| TimescaleDB continuous aggregates and native compression | ADR-0006 | the legal review ADR-0006 asks for; the code deliberately does not use them |
| Rollback of a schema migration (down-scripts) | ADR-0012, ADR-0014, `phase-plan.md` Phase 6 | **stays forward-only by decision**; the guide's backup-and-restore is the answer |
| HMI editor and a component library, scripting (Jint), reporting | `phase-plan.md`, "Later (not yet scoped)" | a phase that creates a concrete need |
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
