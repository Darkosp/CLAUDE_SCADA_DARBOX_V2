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

## 2. Built, but never walked

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
- **Tested, and passing on 2026-10-01.** `tests/Gateway.Tests/EdgeConfigurationPublishingTests.cs`
  runs the publisher against a real in-process MQTT server and asserts the retained
  publish, the late subscriber receiving it, no republication when nothing
  changed, and the emptied topic of a deleted edge — measured here at **3 passed,
  0 failed**. `EdgeConfigurationPayloadTests`, `EdgeConfigurationTests` and
  `BrokerConfigurationTests` cover the payload, the assignment rules and the ACL.
  The publishing tests need no database — only the runtime. The whole run is in
  §2.3 below.
- **Never done:** a run with a **real broker and a real edge** in which the edge
  accepts a derived configuration with **no hand-written file on the plant
  machine** and its readings arrive in the history under the cloud's own tag
  ids. That is ADR-0019's first review criterion, and nothing has met it.
- **Needs:** the cloud stack and an edge container — all of it local, no second
  machine. Docker Desktop is on this machine; see §4 for why a test run cannot
  reach it and why that does not block this.
- **When it exists:** follow *Adding an edge* in
  [`deploy/cloud/README.md`](../../deploy/cloud/README.md), then write it up the
  way the Phase 7 walk is written up, and put the numbers in the phase note.

### 2.2 The database half of the suite, on this machine

- **What happens instead.** A native PostgreSQL 18 service holds port 5432, so
  anything connecting to `localhost:5432` reaches it rather than the container,
  and authenticates as nobody. Every test that needs a live database reports as
  **skipped**, which is the design (a skipped test proves nothing) but means a
  "green" run on this machine is only the part that needs no database.
- **Why `SCADA_TEST_DB_HOST` cannot work around it.** The port is hard-coded to
  5432 in both helpers (`tests/Persistence.Tests/TestDatabase.cs:28`,
  `tests/Gateway.Tests/Hosting/TestDatabases.cs:50`); the variable moves the
  host only.
- **Needs:** either the native service stopped for the duration — an operator's
  call, and this repository has deliberately never stopped it as a side effect
  of anything — or a machine where the two do not collide.
- **Detail:** [`HANDOVER.md`](../../HANDOVER.md) §5, trap 1.

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
| `ScadaDarbox.Gateway.Tests` | 39 | 67 | 106 |
| **all seven** | **260** | **134** | **394** |

The client's own suite, `npm test` in `src/Web`: **53 passed, 0 failed**. The
previous recorded figures — 194 passed / 133 skipped with no database, and 311 /
17 with one — are in `HANDOVER-archive.md`; the counts differ because the
provisioning work added tests, not because tests were lost.

What this run does **not** say: nothing above needed TimescaleDB, so the
database-enforced guarantees (append-only tables, the unique-name indexes, the
migrator's lock) were **skipped**, not verified. Only a run with a reachable
database proves those.

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
