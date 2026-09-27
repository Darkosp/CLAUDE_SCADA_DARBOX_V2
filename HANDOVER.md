# Handover — SCADA_DARBOX

Written 2026-09-27, on `main` at `886d959`, worktree clean, in sync with `origin/main`.

This is a summary for whoever picks the repository up next: what is finished, what the
repository is made of, what it is built from, what is left, and what will bite you. It is a
summary with pointers, not a source of truth — the binding documents remain
[`README.md`](README.md), [`CLAUDE.md`](CLAUDE.md),
[the ADRs](docs/architecture/decisions/README.md) and
[`docs/roadmap/phase-plan.md`](docs/roadmap/phase-plan.md). Every claim below about code names
the file it comes from, and every number marked **measured** was produced on this machine on
2026-09-27 with the command given beside it.

**PR numbers are ambiguous here.** This repository was created on 2026-09-26 with the project's
earlier history pushed into it, so the merge commits carry the predecessor repository's numbers
— #1 to #25 — while this repository's own PRs start again at #1. Where a number below is this
repository's own, it says so; this repository's six are #1 (driver logging), #2 (a test-fixture
guard), #3 (`certs.sh` under `bash`), #4 (`linux-arm64`), #5 (the Journal Note's spacing) and
#6 (ADR-0019's edge assignment and the write it refuses — **open**, the first of this
repository's own PRs whose work is not on `main` yet).
`CLAUDE.md` carries the same warning.

## 1. What is finished

### Phases 0–6.5 — complete, merged to `main`

| Phase | Delivered | Gate and evidence |
|---|---|---|
| 0 — Architecture and foundations | Phase 0 architecture document, ADR-0001…0006, phase plan, `CLAUDE.md` | Gate met: every foundational decision closed and recorded |
| 1 — Core skeleton | Tag engine on PostgreSQL/TimescaleDB, Modbus TCP driver, Gateway (Web API + SignalR), Angular client, DbUp migrations, Modbus simulator, 36 tests | Gate met 2026-09-09 against TimescaleDB 2.17.2 and a live simulator; a device going offline reads **Bad**, not a fabricated zero (ADR-0003) |
| 2 — Tag browsing and device management | Browse tree, device/tag configuration through the UI, dimensioned units, trend chart | Gate met 2026-09-09 (PR #3); walked in the browser, which caught two real defects (a trend chart drawing a line through a Gateway-downtime gap, and a form field shadowing `HTMLFormElement.tagName`) |
| 3 — Alarms | Threshold alarms, states, acknowledge, in-app banner | Gate met 2026-09-10 (PR #5) |
| 4 — Additional drivers and UDTs | OPC UA driver beside Modbus, device templates (UDTs) with live-reference semantics | Gate met 2026-09-11 (PR #6) |
| 5 — Users, roles and security | Opaque session tokens, Site-scoped Viewer/Operator, tenant-wide Admin, append-only `audit_log`, tag write path; migrations moved out of the Gateway | Gate met 2026-09-16 (PR #7), ADR-0011 and ADR-0012 |
| 5.5 — Alarm journal | `alarm_event` append-only journal as the source of truth, live list rebuilt at startup, evaluation start/stop journalled, shelving with a capped expiry, Journal screen | Gate met 2026-09-22 (PR #9), ADR-0013. Alarm state survives a Gateway restart. The hand walk found ten defects the suite had passed |
| 6 — On-premises deployment packaging | `deploy/` Compose file, `.env.example`, `build-images.sh`, `deploy/README.md` (install, upgrade, **tested** rollback); the Gateway serves the built client from its own origin | Gate met 2026-09-23 (PRs #10–#17). 215 .NET + 34 client tests, no skips. ADR-0014: one migrator at a time (advisory lock), a build with zero migration scripts refuses to run; images are built from a commit via `git archive` |
| 6.5 — Names unique within their parent | Uniqueness among live rows, ignoring case, per parent; 409 naming what exists; duplicates renamed on upgrade rather than refused | Gate met 2026-09-24 (PRs #18, #19), ADR-0015. 228 .NET + 38 client tests |

### Phase 7 — cloud topology: every planned step is merged and the gate has been walked

ADRs 0016 (polled vs pushing driver; silence past a staleness limit reads Bad), 0017 (the link:
edge acquires and buffers, does not evaluate alarms; our own payload; Mosquitto over TLS with
a certificate per edge; bounded on-disk buffer that records the window it lost) and 0018 (the
buffer is SQLite, amending ADR-0006's Native AOT assumption) are decided and implemented.

Merged: the pushing contract (PR #20), `Drivers.Mqtt` (PR #21), the edge agent (PR #23), cloud
ingestion (PR #24), TLS with a certificate per edge plus the cloud Compose guide (PR #25); the
driver-logging step beside them (this repository's PR #1); the walk's own defect, `certs.sh`
unparseable under `bash` (PR #3); `linux-arm64` built and run under emulation (PR #4); the
Journal screen's Note printing its parts as one word (PR #5).

The gate was walked by hand, with the link cut for real, and recorded in
[`docs/roadmap/phase-7-manual-gate.md`](docs/roadmap/phase-7-manual-gate.md). What it measured:
a 2 min 5 s outage with the edge off the cloud stack's own network; the readings measured
**inside** that outage stored **after** the reconnect with the edge's own timestamps, all within
one instant; `rows` equal to `distinct_times`; an outage in which nothing was measured adding no
row at all; 190 readings a deliberately lowered bound dropped reported as one `SamplesLost`
entry with its count and both ends. The screen half of the gate was walked on 2026-09-27 as
well: device and tags created through the API the browser itself calls, the client suite re-run,
and a second outage at the default buffer bound — 14 min 42 s, 877 readings per tag, all
measured inside it and stored 56.9 ms after the reconnect.

### What runs right now on this machine — measured

```
wsl docker ps --format '{{.Names}} {{.Ports}}'
```

```
scada-darbox-cloud-gateway-1      0.0.0.0:8080->8080/tcp
scada-darbox-cloud-broker-1       1883/tcp, 0.0.0.0:8883->8883/tcp
scada-darbox-edge-edge-1
scada-timescaledb                 0.0.0.0:5432->5432/tcp
scada-darbox-opcua-sim-1          4840/tcp
scada-darbox-modbus-sim-1         5502/tcp
scada-darbox-cloud-timescaledb-1  5432/tcp          (no published port, by design)
```

So the cloud stack, one edge agent and the on-premises simulators from earlier walks are still
up. The database in `scada-timescaledb` carries PostgreSQL 17.2 with TimescaleDB.

### The test suites — measured 2026-09-27

.NET, `dotnet test --nologo` from the repository root, **with no PostgreSQL reachable from
Windows** (see §5 for why):

| Test project | Passed | Skipped | Failed | Total |
|---|---|---|---|---|
| `ScadaDarbox.Core.Tests` | 100 | 0 | 0 | 100 |
| `ScadaDarbox.Persistence.Tests` | 5 | 57 | 0 | 62 |
| `ScadaDarbox.Gateway.Tests` | 8 | 76 | 0 | 84 |
| `ScadaDarbox.EdgeAgent.Tests` | 15 | 0 | 0 | 15 |
| `ScadaDarbox.Drivers.Modbus.Tests` | 27 | 0 | 0 | 27 |
| `ScadaDarbox.Drivers.Mqtt.Tests` | 26 | 0 | 0 | 26 |
| `ScadaDarbox.Drivers.OpcUa.Tests` | 13 | 0 | 0 | 13 |
| **total** | **194** | **133** | **0** | **327** |

Client, `npm test` in `src/Web`: **47 passed, 0 failed** (the script compiles `models.ts` and
runs `node --test` over `src/Web/tests/*.test.mjs`).

The high skip counts are a design decision, not rot: a test that needs a live database or the
Docker CLI reports as **skipped** rather than passing silently —
`RequiresDatabaseFact`/`RequiresDatabaseTheory` (`tests/Gateway.Tests/Hosting/TestDatabases.cs`,
`tests/Persistence.Tests/TestDatabase.cs`), `RequiresDockerFact`
(`tests/Gateway.Tests/Hosting/BrokerFixture.cs`) and `RequiresDockerComposeFact`
(`tests/Gateway.Tests/ComposeFileTests.cs`). With a database reachable and Docker on the PATH
the same suite is recorded in `phase-plan.md` as **311 passed, 17 skipped across seven
projects, none failed** (the recorded run reported one case more than this one — 328 against
327; I did not chase that difference).

## 2. Project structure

A .NET 10 solution, `ScadaDarbox.slnx`, plus an Angular client, two Compose topologies and
operator guides.

```
src/
  Core/                     domain-neutral contracts and logic; references no protocol,
                            no hosting, no database (ADR-0002)
    Model/                  Tenant, Site, Device, Tag, TagReading, TagValue, Quality,
                            UnitOfMeasure, Folder, DeviceTemplate, AlarmDefinition
    Tags/                   ITagEngine, TagEngine, TagCatalog(+Source), TagSnapshot,
                            PushedSourceRecorder
    Alarms/                 AlarmEngine, Alarm, AlarmEvent (ADR-0013)
    Drivers/                IDeviceDriver, IPushingDeviceDriver (ADR-0016)
    Historian/              IHistorian
    Configuration/          IConfigurationRepositories, ConfigurationConflictException
    Templates/              AddressTemplate (UDT addresses)
    Security/               SessionManager, Session, UserDirectory, UserAccess, SiteRole,
                            ISecurityStore, IAuditLog (ADR-0011)
  Persistence.TimescaleDb/   Npgsql historian, Dapper configuration repositories,
                            DbUp migration runner, embedded migrations
    migrations/0001…0011     0001 initial schema, 0002 nullable value kind, 0003 folders,
                            0004 soft delete, 0005 active tag needs a live device,
                            0006 alarm definition, 0007 device templates,
                            0008 users/sessions/audit, 0009 alarm event,
                            0010 names unique within parent, 0011 edge ingestion
  Gateway/                  ASP.NET host: Web API, SignalR hub, scanning service, security,
                            alarm and configuration endpoints; serves the built Angular client
  Migrator/                 one-shot privileged step that applies the migrations and gives
                            `scada_app` its password; the only holder of the privileged
                            connection string (ADR-0012)
  Modules/
    Drivers.Modbus/         Modbus TCP (NModbus), a 5 s response bound
    Drivers.OpcUa/          OPC UA client (OPC Foundation .NET Standard stack)
    Drivers.Mqtt/           pushing driver (MQTTnet): subscribes, validates, reports
  EdgeAgent/                edge process: Acquisition, Buffer (SQLite), Uplink; self-contained
                            on the CLR, not AOT (ADR-0018)
  Web/                      Angular 22 client (`src/app/*.ts`), node --test suite in `tests/`
tools/
  ModbusSimulator/          demo device: `holding:0` (bar × 100), `coil:0` (pump running)
  OpcUaSimulator/           OPC UA server (PumpNodeManager, SimulatorServer)
tests/                      one project per unit under test (see the table in §1), plus
                            EdgeAgent.CrashWriter, a helper process that dies mid-write
deploy/
  docker-compose.yml        on-premises stack: timescaledb, migrator, gateway, both sims
  .env.example              every variable the stack needs; secrets have no defaults
  build-images.sh           builds every image from a commit (`git archive`), x64 by default;
                            `arm64` builds the edge agent alone
  README.md                 the operator guide: install, upgrade, tested rollback
  cloud/                    cloud stack: timescaledb, migrator, broker (Mosquitto), gateway
    certs.sh                makes the CA and a certificate per edge
    mosquitto/              mosquitto.conf, per-edge ACL, auditing of refusals
    README.md               the cloud guide, including what Git Bash needs
  edge/                     one plant: Compose files for x64 and arm64, .env.example,
                            edge.example.json (the devices and tags an edge reads)
docs/
  architecture/phase-0-architecture.md      components, topologies, data flow, stack
  architecture/decisions/                   ADR-0001…0018 plus the index
  roadmap/phase-plan.md                     the plan, each phase's scope, gate and status
  roadmap/phase-1-running.md                how to run the stack locally
  roadmap/phase-5.5-manual-gate.md          the alarm-journal hand walk
  roadmap/phase-6-manual-gate.md            the deployment hand walk
  roadmap/phase-7-manual-gate.md            the cloud gate walk, its numbers and what it found
```

Five images are built, each with a `Dockerfile` beside its project: `src/Migrator`,
`src/Gateway`, `src/EdgeAgent`, `tools/ModbusSimulator`, `tools/OpcUaSimulator`. There is no
client image — the Gateway's image compiles the Angular client and serves it from its own
origin, which is why the client has no CORS allowance and no Gateway URL in it.

Two rules hold the layout together, both from ADR-0002: Core never references a specific
protocol, a hosting layer or a database, and modules are composed at compile time (project
references), never loaded by reflection. The Gateway and `Persistence.TimescaleDb` sit outside
Core for that reason.

## 3. Technologies, libraries and interfaces

### The stack (ADR-0006, and every ADR that amends it)

| Layer | Choice | Version, or where it is pinned |
|---|---|---|
| Runtime | .NET (LTS) | `net10.0` on every project; SDK 10.0.401 on this machine |
| Server | ASP.NET Core Web API + SignalR | `src/Gateway` |
| Client | Angular, standalone components, TypeScript | Angular 22.1, TypeScript 6.0, RxJS 7.8, `@microsoft/signalr` 10 |
| Database | PostgreSQL + TimescaleDB | `timescale/timescaledb:2.17.2-pg17` (root `docker-compose.yml` and both deploy stacks) |
| Historian schema | plain TimescaleDB hypertables only | no continuous aggregates, no native compression — TSL-licensed, pending the legal review recorded in ADR-0006 |
| Migrations | DbUp, numbered embedded SQL scripts | `dbup-postgresql` 7.0.1, `src/Persistence.TimescaleDb/migrations/` |
| Configuration data access | Dapper over Npgsql | `Dapper` 2.1.79, `Npgsql` 10.0.3 (ADR-0008) |
| Modbus | NModbus | 3.0.83 |
| OPC UA | OPC Foundation .NET Standard stack | `Opc.Ua.Client` / `Opc.Ua.Configuration` 1.5.378.156 |
| MQTT | MQTTnet (client and, in tests, server) | 5.2.0.1603 |
| Edge buffer | SQLite | `Microsoft.Data.Sqlite` 10.0.11 (ADR-0018) |
| Broker | Eclipse Mosquitto | `eclipse-mosquitto:2.1.2-alpine`, TLS on 8883 |
| Tests (.NET) | xUnit + coverlet | xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 |
| Tests (client) | `node --test` after `tsc` (Vitest and jsdom are present for tooling) | `src/Web/package.json` |
| Packaging | Docker Compose, images built with BuildKit from `git archive` of a commit | `deploy/build-images.sh` |
| Not yet in use, decided for later | Jint for scripting, a HMI component library, reporting | ADR-0006; "Later (not yet scoped)" in `phase-plan.md` |

No ORM beyond Dapper's configuration CRUD, no message queue other than MQTT, no
Kubernetes — each of those needs an ADR before it appears (CLAUDE.md, "When Phase 1 (or any
phase) begins").

### Interfaces other code depends on

**Web API** (`src/Gateway`, session token as `Authorization: Bearer`, everything except health
and login requires a session; a Site the caller cannot see answers 404, never 403):

| Method and path | Purpose |
|---|---|
| `POST /api/auth/login`, `/api/auth/logout`, `GET /api/auth/me` | sessions and the caller's own access |
| `GET /api/health` | the only anonymous endpoint besides login |
| `GET /api/tags`, `GET /api/tags/{tagId}` | current value of every tag the caller may see |
| `GET /api/tags/{tagId}/history?from=&to=` | historised samples |
| `POST /api/tags/{tagId}/value` | tag write, Operator-gated |
| `GET /api/alarms`, `GET /api/alarms/journal?from=&to=&limit=` | live list; the journal, newest first, `limit` default 200, capped 1000 |
| `POST /api/alarms/{definitionId}/acknowledge`, `/shelve` | acknowledge; shelve with a bounded expiry |
| `GET|POST|PUT|DELETE /api/tags/{tagId}/alarms[/{definitionId}]` | threshold definitions |
| `GET /api/sites`, `GET /api/sites/{siteId}/tree`, `GET /api/drivers` | browse tree and driver configuration shapes |
| `POST|PUT|DELETE` on `/api/sites/{siteId}/folders`, `/devices`, `/devices/{deviceId}/tags` | configuration CRUD (409 on a name conflict, ADR-0015) |
| `GET|POST|DELETE /api/templates…`, `POST /api/sites/{siteId}/devices/from-template` | device templates (UDTs, ADR-0010) |
| `/api/users…` | user administration: create, delete, make Admin, set password, grant/revoke a Site role |

**SignalR** — hub path `/hubs/tags` (`HubQueryToken.HubPath`), authorized, one group per Site.
Server-to-client methods: `tagValues`, `alarms`, `accessChanged`. Client-to-server pull
methods: `GetCurrentValues()`, `GetCurrentAlarms()`, so a client that connects between scans
renders immediately. The token is accepted in the query string for the hub path only.

**MQTT** (`src/Modules/Drivers.Mqtt`, `src/EdgeAgent`) — the edge publishes to
`{TopicPrefix}/{Edge:Id}/samples` with `TopicPrefix` defaulting to `scada/edge`, QoS
At-Least-Once; the Gateway subscribes to that filter for the device and stores a batch before
acknowledging it, so ingestion is idempotent per (tag, source timestamp). The edge's id is the
name in its certificate, and the broker's ACL lets it publish under that name only; a refusal
is audited. A batch that cannot be stored is deliberately **not** acknowledged, so the broker
delivers it again.

**Configuration surfaces.** The Gateway reads `src/Gateway/appsettings.json`
(`ConnectionStrings:ScadaDb` without a password, `Sessions`, `PushedSources:ClockSkewTolerance`,
`Modbus:Host`/`Port` for the demo seeder's device) and these environment variables:
`SCADA_APP_DB_PASSWORD`, and — only while no user exists — `SCADA_INITIAL_ADMIN_USERNAME` /
`SCADA_INITIAL_ADMIN_PASSWORD`. The Migrator takes `SCADA_MIGRATOR_CONNECTION` and
`SCADA_APP_DB_PASSWORD`; it is the only component given the privileged credential (ADR-0012).
The edge takes `Edge__Id`, `Edge__Broker__Host|Port|CaFile|CertFile|KeyFile`,
`Edge__Buffer__Path`, `Edge__Buffer__MaxPendingSamples` and `Edge:Devices[]` (name, driver key,
scan interval, settings, tags with the **Gateway's** tag ids) — see
`deploy/edge/edge.example.json` and `src/EdgeAgent/EdgeOptions.cs`. The Compose stacks take
`deploy/.env` (template: `deploy/.env.example`): `SCADA_IMAGE_TAG`, `SCADA_DB_ADMIN_PASSWORD`,
`SCADA_APP_DB_PASSWORD`, `SCADA_INITIAL_ADMIN_*`, `SCADA_HTTP_PORT` (8080 by default). Ports in
use: 8080 (Gateway and web client), 5432 (PostgreSQL, development only), 5502 (Modbus
simulator), 4840 (OPC UA simulator), 8883 (Mosquitto over TLS).

## 4. What is next

### Phase 7's own remainder

The plan's step 6 is "the hand walk, with the link cut for real". It has been walked by hand,
on one machine, and it now has its own record — including a separate walk of the screen half
that step 5 needs. What is **still open**, in the order `CLAUDE.md` states it:

1. **A link between two hosts.** Every outage walked so far was a Docker network disconnect on
   a single machine: the edge container and the cloud stack on the same host, the link cut by
   detaching the edge from the cloud network. Nothing has yet crossed a real network boundary,
   with real latency, a real TLS handshake over a wire, and a broker on another machine.
2. **Two clocks.** Source timestamps have only ever come from the same machine's clock. The
   skew path exists and is tested (`ClockSkewTolerance`, 30 s, and the journal entry an
   over-tolerance edge produces), but no walk has had two machines whose clocks actually
   disagree.
3. **A real arm64 board.** `linux-arm64` is built and has run under QEMU emulation only
   (this repository's PR #4; numbers in the gate record). Nothing has run on plant hardware.

Also worth knowing before touching the link: the edge's device and tag list — including the
**cloud** tag ids — is still copied to the edge by hand today. How it reaches an edge is no
longer open: [ADR-0019](docs/architecture/decisions/0019-edge-configuration-provisioning.md)
decides that the cloud is the source of truth and derives each edge's configuration onto the
link the edge already holds. It is **Accepted and not yet built**, so the hand-written file
stands until those slices land.

### Deferred, with the phase that must pick it up

| Deferred item | Where it is recorded | Needs before code |
|---|---|---|
| Filtering the alarm journal (by tag, event type, time) | `phase-plan.md`, "Deferred out of Phase 5.5, raised by walking its gate by hand" | a phase that wants it |
| Alarm flapping — deadband, on-delay | same place | **an ADR first** |
| Rollback of a schema migration | `phase-plan.md` Phase 6 note; ADR-0012 | stays forward-only by decision — the guide's backup-and-restore step is the answer |
| Continuous aggregates and native compression in the historian | ADR-0006, `phase-plan.md` Phase 1 constraint | the legal review ADR-0006 asks for; the code deliberately does not use them |
| Alarm notification channels (email/SMS/push) and escalation policy | `phase-0-architecture.md` "decisions not yet made"; out of scope in Phase 3 | a design decision, then an ADR |

### Not scoped yet — deliberately

`phase-plan.md`'s "Later (not yet scoped)": the **HMI screen editor** and a real component
library, the **scripting engine (Jint)**, and **reporting**. ADR-0002's module discipline is the
reason they wait: they are added for a deployment that needs them, not in anticipation.

### Open decisions a new task may run into

`phase-0-architecture.md` lists what has deliberately not been decided: the auth mechanism
beyond Phase 5's sessions, HMI editor details, alarm notification channels and escalation, and
any compliance/regulatory module. `CLAUDE.md` is explicit about the correct move when a task
seems to need one of them: raise it in the design conversation, do not decide it inside an
implementation PR. The same rule applies to anything that would contradict an ADR — an ADR is
changed by a new ADR that supersedes it, never edited into a different decision.

### First hour of the next session

1. Read `CLAUDE.md`, the ADR index, `phase-plan.md` (Phase 7's scope and status) and the Phase 7
   gate record. Everything else follows from those.
2. Fix the local database/port trap first (§5) or the integration half of the suite will stay
   skipped and "green" will mean less than it looks.
3. Check `git status` before assuming any document was committed. Design-conversation documents
   are written to disk by a session that cannot run git; a document on disk is not a commit.
4. Use **Windows git** in this working copy (see §5), and remember that implementation work
   goes through a PR while documentation goes straight to `main`.

## 5. Known problems, gaps and traps

Nothing in this section is hidden — most of it is already recorded in the phase notes — but a
new session that does not know it will misread a green test run, break the build scripts, or go
looking for defects that were fixed months ago.

### Traps measured on this machine, 2026-09-27

**1. A native PostgreSQL on Windows holds port 5432, so the development database's published
port does nothing.** This is the one that matters most, because it silently deletes the
integration half of the test suite.

```
Get-NetTCPConnection -State Listen -LocalPort 5432
  LocalAddress  LocalPort  OwningProcess  Process
  ::            5432       12404          postgres
  0.0.0.0       5432       12404          postgres

Get-Service postgresql-x64-18    ->  Running   (PostgreSQL Server 18)
```

`wsl docker ps` shows `scada-timescaledb  0.0.0.0:5432->5432/tcp`, but the native server won the
port, so anything connecting to `localhost:5432` reaches *it*:

```
# the credentials the tests use, against localhost:5432
host=localhost port=5432 user=scada password=scada
-> PostgresException 28P01: password authentication failed for user "scada"
```

The container's own database is healthy — `wsl docker exec scada-timescaledb psql -U scada -d
scada -c 'select version()'` answers `PostgreSQL 17.2 ... 64-bit` — it is only the published port
that is unusable. The consequence is the skip column in §1: 57 of 62 Persistence tests and 76 of
84 Gateway tests reported as skipped rather than passing. `deploy/README.md` documents this exact
class of failure for port 8080 (Docker not refusing a port another program holds, one listener on
IPv4 and another on IPv6); here it has happened on 5432. **The test projects hard-code port
5432** (`Host={SCADA_TEST_DB_HOST};Port=5432`, `tests/Persistence.Tests/TestDatabase.cs`,
`tests/Gateway.Tests/Hosting/TestDatabases.cs`), so the only way to get the integration tests
running is to free 5432 — stop the native service for the duration, or reach the container
through a path that does not pass through the Windows host's port (the `SCADA_TEST_DB_HOST`
override exists for exactly this). **I did not stop the native service**: it is a
machine-level service that was there before this session, and stopping it is the operator's
call, not a side effect of writing a handover.

**2. There is no `docker` CLI on the Windows PATH; `docker compose` and `docker version` fail.**
Everything works through WSL instead (`wsl docker ps`, `wsl docker compose version` →
`Docker Compose version 2.40.3+ds1-0ubuntu1`). The broker tests need `docker version`
(`RequiresDockerFact`, `tests/Gateway.Tests/Hosting/BrokerFixture.cs`) and the Compose file tests
need `docker compose version` (`RequiresDockerComposeFact`,
`tests/Gateway.Tests/ComposeFileTests.cs`); run from Windows PowerShell they all report as
skipped. `deploy/README.md` assumes Docker Desktop is on the PATH, which on this machine means
running the guide from a shell that has it.

**3. LF and CRLF.** The repository has no `.gitattributes`, committed blobs are LF and the
working tree is CRLF. Two consequences: WSL's `git` in the same directory reports a large number
of spurious local modifications (it was 287 files when this was last checked), so **use Windows
git** — `/mnt/c/Program Files/Git/cmd/git.exe` — for `status`, `diff`, `log` and `commit`; and
shell scripts with CRLF break `bash`, which is how `deploy/cloud/certs.sh` failed in the Phase 7
walk (`set -euo pipefail` with a trailing CR parses as an unknown command name). That defect is
fixed in this repository's PR #3, and `deploy/cloud/.gitattributes` exists for that
subdirectory, but the general hazard stands: strip the CRs before running a script with WSL
`bash` (`tr -d '\r' < script.sh > /tmp/script.sh`), which is also needed for
`deploy/build-images.sh` line 27 on a fresh checkout.

**4. `deploy/.env` and `deploy/cloud/.env` exist locally and are correctly ignored.** No
non-example `.env` is tracked (`git ls-files` lists only the three `.env.example` files), so
secrets are not in the history — but they *are* on this machine, and the stacks running from
`deploy/` use them. The cloud stack was also started from a second copy of the repository at
`/root/deep-scada-darbox` inside WSL (`com.docker.compose.project.config_files` on
`scada-timescaledb`), which is worth knowing before assuming a running container reflects the
working tree you are looking at.

### Defects already found and fixed — do not go hunting for them

Each of these was real, each was caught by doing something the suite could not, and each is
recorded in the phase notes. They are listed because the *patterns* are the project's recurring
failure modes, not because the code is still wrong.

| Defect | Found by | Now |
|---|---|---|
| The Modbus driver fabricated `NaN`/`false` for unreadable values | the Phase 1 gate, device offline | Values are genuinely nullable end to end when quality is Bad (ADR-0003) |
| The trend chart drew a straight line through a Gateway-downtime gap | using the chart in a browser (Phase 2) | Fixed; the same class of mistake ADR-0003 exists to prevent, in a different layer |
| A form field named `tagName` shadowed `HTMLFormElement.tagName` | the same walk | Renamed |
| Alarm state did not survive a Gateway restart | Phase 5.5's gate | The `alarm_event` journal is the source of truth (ADR-0013) |
| Ten defects found by walking Phase 5.5's gate by hand | hand walk | All fixed; the phase note lists them |
| A duplicate `Pump House` name existed in real data | the Phase 6.5 upgrade, run against the real database | Renamed on upgrade and audited, not refused (ADR-0015) |
| `deploy/cloud/certs.sh` could not be run with `bash` and had no executable bit | the Phase 7 walk | Fixed (this repository's PR #3) |
| A Modbus read had **no timeout** — a device that accepts the connection and then answers nothing held its scan loop while already-read tags kept their values, which looks like a live plant | the driver-logging step | `ResponseTimeout = 5 s` on read and write (`src/Modules/Drivers.Modbus/ModbusTcpDriver.cs:51`). Whether it should be a driver constant rather than a setting is **open** — that is a design question, not an oversight |
| The Journal screen printed a Note's parts as one word | the Phase 7 screen walk | Fixed (this repository's PR #5) |

The working rule the project drew from this — stated in `CLAUDE.md` — is that a phase with a
screen is not done until someone has used the screen, and Phase 5.5's walk is the evidence.

### Genuinely open limitations

- **No CI.** There is no `.github/` directory and no other pipeline: every build, migration and
  test run is done by hand, and the "gate met" claims rest on a recorded hand walk. Adding a
  pipeline is not scoped by any phase.
- **The edge's device/tag list is copied by hand**, and it must name the *cloud* Gateway's tag
  ids (`src/EdgeAgent/EdgeOptions.cs` says so explicitly). What happens when one side's list is
  edited and the other is not is answered by
  [ADR-0019](docs/architecture/decisions/0019-edge-configuration-provisioning.md) — the cloud
  derives the list and publishes it, so there is only one side to edit — but the answer is
  **not built yet**, so until it is the two lists can still drift.
- **A test run without a database is not the same evidence as one with it.** §1 shows the shape:
  133 of 327 .NET tests report as skipped on this machine. Treat any "all green" claim as
  conditional on which of the three prerequisites (database, `docker version`, `docker compose
  version`) were actually available, and say which.
- **The historian grows continuously** — the seeded device scans every 1000 ms and every reading
  is written, so a row count is a snapshot at that instant, not a stable figure. The Phase 7 gate
  record is careful about this ("`rows` equal to `distinct_times`", "all within one instant"); a
  new measurement must be equally careful.
- **The edge agent is not Native AOT** (ADR-0018): the OPC UA stack builds objects by reflection
  and fails under AOT before it reaches the network. Native AOT was tried and abandoned; the
  image is bigger and starts slower than the original plan assumed, and `linux-arm64` is only
  verified under emulation.
- **Alarms have no notification channel.** Shelving is time-bounded and chosen from a fixed list;
  acknowledge/shelve are the only ways a human resolves an alarm. Email/SMS/push and escalation
  are open decisions (§4).
- **MQTT and OPC UA are the only drivers.** RTU, IEC 60870-5-104, DNP3 and the rest are not
  scoped; a new protocol is a new module behind ADR-0002's boundary, and its timestamp semantics
  have to respect ADR-0003 (for Modbus the documented answer is the time of a successful read).

### Appendix — the commands behind the measurements

```powershell
# from the repository root, Windows PowerShell

# the suites (§1)
dotnet test --nologo                                  # 327 cases: 194 passed, 133 skipped, 0 failed
cd src/Web ; npm test                                 # 47 passed, 0 failed

# why the skips (§5)
Get-NetTCPConnection -State Listen -LocalPort 5432    # a native postgres, not Docker
Get-Service | Where-Object Name -match postgres       # postgresql-x64-18, Running
wsl docker ps --format '{{.Names}} {{.Ports}}'
wsl docker exec scada-timescaledb psql -U scada -d scada -tAc 'select version()'

# the repository, with the git that agrees with it (§5)
& 'C:\Program Files\Git\cmd\git.exe' status --short --branch
& 'C:\Program Files\Git\cmd\git.exe' log --oneline -10
```

To run the whole thing locally instead, `docs/roadmap/phase-1-running.md` is the sequence
(database → migrator → simulator → Gateway → client), and `deploy/README.md` is the packaged
one. The Gateway refuses to start unless the schema matches its build exactly and will refuse a
privileged connection string; that refusal is the feature, not a problem to work around.






