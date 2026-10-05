# Handover — SCADA_DARBOX

Written 2026-09-27, on `main` at `886d959`, worktree clean, in sync with `origin/main`.

*Amended 2026-10-02, on `main` at `df191b3`: ADR-0019 §8 (an edge declares its own drivers, and the
cloud refuses what it cannot read) is built and walked, and §5 gained the section a second machine
needs — what travels through git and what has to exist again on each machine. The file is worked on
from a work computer and a home one, so read that section before assuming your checkout is the
state.*

*Amended again 2026-10-02, on `main` at `a40b457`: a session that began by walking Phase 7's gate on
**two hosts** went on to close three decisions. Read §6 before doing anything — it is the shortest
route to what is true now and what is next, and this file's own structural inventory is the part of
the repository most likely to be stale.*

*Amended again 2026-10-03, on `main` at `75845c9`: a later session closed **ADR-0023** — the write
path — which is the first thing in this project that lets the cloud change a plant. §6 carries it,
and §2's structure list now ends at migration 0016 and ADR-0023.*

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
#6 (ADR-0019's edge assignment, its API and the screen that assigns them — **merged** as
`fe829f0`, the first of this repository's own PRs whose work went to `main` through a squash).
`CLAUDE.md` carries the same warning.

## 1. What is finished


The phase-by-phase record that used to live here - Phases 0-6.5, Phase 7, ADR-0019, the measured
state of this machine and the test baselines - now lives in [`HANDOVER-archive.md`](HANDOVER-archive.md).
It was moved, not deleted or rewritten. Read it only when you need the history.
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
    migrations/0001…0016     0001 initial schema, 0002 nullable value kind, 0003 folders,
                            0004 soft delete, 0005 active tag needs a live device,
                            0006 alarm definition, 0007 device templates,
                            0008 users/sessions/audit, 0009 alarm event,
                            0010 names unique within parent, 0011 edge ingestion,
                            0012 edge assignment, 0013 edge link device,
                            0014 the edge's declared driver keys (ADR-0019 §8),
                            0015 the devices an edge cannot read (ADR-0021),
                            0016 an edge's link limits (ADR-0022)
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
tests/                      one project per unit under test (the suite table is in
                            HANDOVER-archive.md), plus EdgeAgent.CrashWriter, a helper
                            process that dies mid-write
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
  edge/                     one plant: Compose files for x64 and arm64, .env.example.
                            No device file: what an edge reads is derived by the cloud
                            and published on the edge's own topic (ADR-0019)
docs/
  architecture/phase-0-architecture.md      components, topologies, data flow, stack
  architecture/decisions/                   ADR-0001…0022 plus the index
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
`Edge__Buffer__Path` and `Edge__Buffer__MaxPendingSamples`, and nothing else: there is no
`Edge:Devices` any more, because what an edge reads is derived by the cloud and published on
`{TopicPrefix}/{Edge:Id}/config`, retained, over the link it already holds (ADR-0019) — see
`src/EdgeAgent/EdgeOptions.cs`. The Compose stacks take
`deploy/.env` (template: `deploy/.env.example`): `SCADA_IMAGE_TAG`, `SCADA_DB_ADMIN_PASSWORD`,
`SCADA_APP_DB_PASSWORD`, `SCADA_INITIAL_ADMIN_*`, `SCADA_HTTP_PORT` (8080 by default). Ports in
use: 8080 (Gateway and web client), 5432 (PostgreSQL, development only), 5502 (Modbus
simulator), 4840 (OPC UA simulator), 8883 (Mosquitto over TLS).

## 4. What is next

### Phase 7's own remainder

**Read §6 first: it is the state at `a40b457` and it supersedes what follows where they disagree.**
The decisions that were open when this section was written — ADR-0020, ADR-0021 and ADR-0022 — are
now decided, implemented and verified; `open-work.md` §3 is the register that was kept current.

The plan's step 6 is "the hand walk, with the link cut for real". It has been walked by hand,
on one machine, and it now has its own record — including a separate walk of the screen half
that step 5 needs. **Items 1 and 2 below were walked on two machines on 2026-10-02** and are
closed; the record is
[the two-host walk](docs/roadmap/phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02).
They keep their numbers and their original text, struck through, because that text was true for
months and a reader who remembers it needs to see what changed. What is **still open** is item 3:

1. ~~**A link between two hosts.** Every outage walked so far was a Docker network disconnect on
   a single machine: the edge container and the cloud stack on the same host, the link cut by
   detaching the edge from the cloud network.~~ **Walked 2026-10-02.** The outage was a real
   network boundary — the edge host's WiFi adapter disabled for **2 min 2 s**, not a Docker
   disconnect. **119 readings measured inside it** reached the history with their own
   timestamps, none invented, none duplicated, all 379 rows with 379 distinct times. The two
   ends saw it differently, which is why the item existed: the broker logged the client gone
   only at `17:01:14`, as `disconnected: exceeded timeout` — **20 s after** the adapter was
   disabled, and the edge never hung on the dead socket. **What it did not do:** the two hosts
   were on one `/24`, so a NAT that drops a *mapping* still has not been produced by a walk.
2. ~~**Two clocks.** Source timestamps have only ever come from the same machine's clock. The
   skew path exists and is tested (`ClockSkewTolerance`, 30 s, and the journal entry an
   over-tolerance edge produces), but no walk has had two machines whose clocks actually
   disagree.~~ **Walked 2026-10-02.** A staged **+45 s** on the edge host, against the 30 s
   tolerance, produced one `SourceClockSkew` row (`clock_skew_seconds = 46.8458066`) and
   **136 samples stored with the edge's times**, 46.8 s ahead of the cloud host's own clock —
   the edge's clock neither trusted nor corrected (ADR-0017). It also settled what the standing
   `ingested_at − source_time` of about −1.8 s is: a clock offset plus pipeline delay, observed
   rather than set, because neither host had a working NTP source.
3. **A real arm64 board.** `linux-arm64` is built and has run under QEMU emulation only
   (this repository's PR #4; numbers in the gate record). Nothing has run on plant hardware.
   This is the last item, and it needs the board.

Also worth knowing before touching the link: the edge's device and tag list — including the
**cloud** tag ids — is no longer typed by hand anywhere. How it reaches an edge is no longer
open: [ADR-0019](docs/architecture/decisions/0019-edge-configuration-provisioning.md) decides
that the cloud is the source of truth and derives each edge's configuration onto the link the
edge already holds. **The assignment half is built and merged** — an edge is an entity of the
tenant, a device may be assigned to one by an ordinary edit of the device, and the Gateway stops
polling and refuses to write a device an edge reads (PR #6, `fe829f0`). **The edge's half of
provisioning is built** (2026-09-28): the uplink subscribes to this edge's own retained config
topic on the connection it already holds, `EdgeConfigurationConsumer` accepts a
`EdgeConfigurationPayload` whole or not at all, the last accepted configuration is kept in the
buffer beside the samples, and a newer one is applied by restarting acquisition — an edge that
has accepted none reads nothing rather than something typed by hand. `Edge:Devices` is gone from
`EdgeOptions`, from `src/EdgeAgent/appsettings.json`, from `deploy/edge/edge.example.json`
(deleted) and from the edge Compose file. ~~What is still open is the walk, not the cloud's half.~~
**Walked 2026-10-02, and on two hosts** — the edge started with `0 device(s)`, accepted the
cloud-derived revision, and logged `Connected to device Pump Station PLC` with nothing on that
machine naming a device, an address or a tag id
([the record](docs/roadmap/phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02)).
What that walk did **not** test is a device assigned while the edge is connected, or a
configuration changing under a running edge; both edges accepted one revision and kept it.
**Corrected 2026-10-01: this
paragraph said the cloud's half was not built, and it was** — `cf457da` and `487e07d`
committed it on 2026-09-28/29, and the prose written the next morning did not notice. The
Gateway derives each edge's devices from the catalogue (`EdgeConfigurationBuilder`), publishes
them retained on `{prefix}/{edgeId}/config` (`EdgeConfigurationPublisher`, registered in
`GatewayApp` and turned on by `deploy/cloud/docker-compose.yml`), and
`deploy/cloud/mosquitto/acl` lets `scada-gateway` write that topic while each edge reads only
its own. ~~What has never been done is a run with a real broker and a real edge accepting a
derived configuration end to end.~~ **Done 2026-10-02, on a second host**: a real Mosquitto over
real TLS, a real Gateway, and a real edge agent that started from `0 device(s)` and accepted the
revision the cloud derived. Every unfinished item, with what it waits for, is in
`docs/roadmap/open-work.md`.

**Added 2026-10-02: the edge declares its own drivers, and the cloud refuses what it cannot read
(ADR-0019 §8).** The walk of the provisioning step found a device whose `driverKey` no edge driver
answered to accepted, derived, published, and refused only by the edge — loud at the plant, silent
in the cloud. The edge now publishes `EdgeDriversPayload` (Drivers.Mqtt, version 1) retained on
`{prefix}/{edgeId}/drivers` as it connects; `EdgeDriverDeclarations` reads it on the provisioning
connection the publisher already holds, `EdgeRepository.RecordDriversAsync` stores it (migration
`0014`: `edge.driver_keys text[]`, `edge.drivers_declared_at`), and a device assigned to an edge
that has declared it does not have that driver is refused by name at the save that assigns it
(`EdgeEndpoints.ProblemWithEdgeDrivers`). An edge that has declared nothing is accepted and shown
as having declared nothing — null is not empty — and a declaration that omits an already-assigned
device's driver is audited with the device named and the assignment left alone. The tests are in
`Drivers.Mqtt.Tests` (the format), `EdgeDriverDeclarationsTests` and `EdgeDriverRefusalTests` (the
Gateway's halves), `EdgeAssignmentTests` (the column) and `BrokerConfigurationTests` (the ACL for
the new topic). **Walked 2026-10-02**, on one machine with the real cloud stack: the edge declared
its two drivers at `15:36:07.811`, the cloud read them 0.019 s later, `/api/edges` answered
`["modbus-tcp","opc-ua"]` where it had answered `null`, the save that had been accepted before the
declaration was then refused `400` naming the edge and the drivers it has, and the one device
assigned before the declaration was named in the log and in one audit row with a null actor. The
record, with the output, is
`docs/roadmap/phase-7-manual-gate.md#since-the-walk-driverkey-declared-by-the-edge-and-refused-by-the-cloud-2026-10-02`.
What the walk also found, and left open, is a device with no tags derived into a configuration the
edge refuses whole, recorded in `open-work.md` with the decision it waits for.

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

1. Read `CLAUDE.md`, the ADR index, `phase-plan.md` (Phase 7's scope and status), the Phase 7
   gate record, and [`docs/roadmap/open-work.md`](docs/roadmap/open-work.md) — the register of
   everything unfinished, each item naming what it waits for. If this is **not** the machine that
   wrote the last note, read §5's *Two machines* first; it is short and it saves the rest.
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

### Two machines: what travels through git, and what does not (2026-10-02)

This project is worked on from more than one machine (a work computer and a home one), which is
why `docs/roadmap/open-work.md` exists at all. **The repository is the handover**: everything a
session needs to know is a file in it, and `git pull` is the whole transfer of state. What follows
is the short list of things that are *not* in the repository and have to exist again on each
machine. None of it is project state — all of it is regenerable in minutes.

| Not in git | Why, and what to do on the next machine |
|---|---|
| `deploy/.env`, `deploy/cloud/.env`, `deploy/edge/.env` | Ignored, deliberately (they hold passwords). Copy from each `.env.example` and fill in. The values one walk used — `SCADA_HTTP_PORT=8098`, `SCADA_BROKER_PORT=8885`, image tag, cert dir — are in the Phase 7 gate record's walk sections. |
| `src/Web/node_modules` | Ignored. `npm install` in `src/Web`. The client's `npm test` script compiles `models.ts` with `tsc` first, so it needs them; without them it fails with `'tsc' is not recognized`. |
| Certificates | Made, never committed, and `ca.key` must stay on the one machine that signs. On a second machine: copy `ca.crt` plus that machine's own certificate and key, or re-run `deploy/cloud/certs.sh client <name>` where the CA is. `certs.sh expiry` lists every date. |
| Docker images | `deploy/build-images.sh HEAD` on the machine that runs them, or `docker save`/`docker load`. They are tagged with the commit they came from, so a stale image is visible rather than silent. |
| A database for the suite | A container with `SCADA_TEST_DB_PORT` set to a port the host actually answers on (trap 1 above), or the integration half of the suite skips and "green" means less than it looks. |
| Running stacks | `docker compose … up -d` per topology; nothing about a running container is remembered anywhere. |

**Added 2026-10-02, from the two-host walk: what the table above understates.** Every line below
was met while putting a second Windows machine on the link, and none of it is in the repository.
None of it is a defect either — it is what two machines cost.

| Trap | What happens, and what to do |
|---|---|
| **`SCADA_EDGE_CERT_DIR` does not travel.** | It names a path *on the machine that holds the certificates*. Copying a filled `deploy/edge/.env` from the cloud host to the edge host carries the cloud host's path, and the container then mounts nothing and dies on a missing file. Rewrite that one line on the second machine. |
| **`hosts` needs an elevated shell, and `Set-Date` does too.** | Both fail with "Access is denied" / "A required privilege is not held by the client" from an ordinary prompt — and an ordinary prompt that happens to be *in* `C:\Windows\System32` still is not elevated. Use `Start-Process powershell -Verb RunAs`. |
| **Docker Desktop may not put `docker` on `PATH` in the session you have open.** | `'docker' is not recognized` while Docker Desktop is running and healthy. A new terminal fixes it; so does the full path, `"$env:ProgramFiles\Docker\Docker\resources\bin\docker.exe"`. Do not conclude Docker is missing. |
| **A closed port proves nothing.** | `Test-NetConnection <host> -Port 8883` failing *before the broker is up* reads as a blocked network, and `Ping` failing alongside it reads as client isolation. It is neither: Windows Firewall drops ICMP by default. Prove the path with something listening — a temporary `TcpListener` on the far host — rather than inferring it from a closed port. On this walk the network was fine and the port simply had nothing behind it. |
| **Do not add a firewall rule for the broker by hand.** | It is refused from a non-elevated shell, and it is not needed: Docker Desktop's own inbound rules for `com.docker.backend.exe` carry the published port. Check for them before concluding the port is closed. |
| **`gunzip \| docker load` through Git Bash failed here** (`archive/tar: invalid tar header`) on an archive whose SHA256 matched the source exactly. | The archive was fine. `docker load -i <file>.tar` — the uncompressed one, from PowerShell, no shell in the middle — worked. Prefer it, and transfer the `.tar` if the pipe is the only thing that breaks. |
| **`openssl` is fine in Git Bash but not in PowerShell, and `OPENSSL_CONF` breaks it even there.** | This machine has `OPENSSL_CONF` pointing at `C:\Program Files\PostgreSQL\psqlODBC\etc\openssl.cnf`, which does not exist. `openssl version` still succeeds — it does not read the config — while every `req`/`x509` call fails on it. `certs.sh` names the failure now; the fix is `unset OPENSSL_CONF` in the same bash invocation. |
| **Line endings break a generated script's heredoc.** | A `.ps1` written through a CRLF pipe can carry a stray CR into a quoted command and corrupt it silently. Prefer `docker load -i` over a piped shell command, and write scripts with `Set-Content` rather than pasting a heredoc through several layers. |

The two listed in the table above that this walk **did not** need: no `npm install` (the edge host
runs no client), and the certificates were reused from the cloud host's existing CA rather than
made again — `certs.sh client <edge-id>` adds an identity to a CA that already exists, which is
the cheap path and the one to take. `ca.key` was verified absent on the second machine before the
walk started, not after.

**Left running on this machine after that walk, 2026-10-02.** None of it is in git and none of it
survives a reboot, which is the point of writing it down — a session that finds a stack already
up should know what put it there.

| What | State |
|---|---|
| `scada-darbox-cloud` stack | **Up**: `timescaledb` (healthy), `broker` (8883), `gateway` (8080). `migrator` `Exited (0)`, which is correct. |
| `scada-walk-modbus-sim` | **Up** on 5502, published on every interface, on the cloud stack's network as `modbus-sim`. Not part of either Compose file — a plain `docker run` made for the walk. `deploy/README.md`'s own simulator belongs to the on-premises stack. |
| `scada-test-db-5433` | **Up**, `scada/scada`, so `SCADA_TEST_DB_PORT=5433 dotnet test ScadaDarbox.slnx` runs the integration half instead of skipping it. The native PostgreSQL 18 service still holds 5432 (trap 1 below); neither gives way. |
| Certificates | `C:\Users\darko\scada-certs` — **this is the active CA**, the one the running broker serves. It holds `broker`, `scada-gateway`, `plant-7` and `plant-b`. `ca.key` is here and must stay here. |
| The edge host's certificates | `C:\_walk-transfer\certs` on the second machine, plus that machine's `C:\DEEP_SCADA_DARBOX` at `57025c7`. |

**The WSL copy is gone, deliberately.** Until 2026-10-02 this repository also existed at
`/root/deep-scada-darbox` inside WSL, with its **own, different CA** at `/root/scada-certs` —
same subject (`CN=SCADA_DARBOX broker CA`), different key, so a certificate from one is refused by
the other with nothing on screen naming the reason. `deploy/cloud/.env` still pointed
`SCADA_CERT_DIR` at that WSL path, which is why the cloud stack was once started from a second
copy of the tree. Both were removed: the repository copy because git is the source of truth and a
second checkout only drifts, and the certificates after copying them to
`/root/scada-certs-superseded-2026-10-02`. **If a container reports a certificate it should trust,
check which CA signed the file it was handed before looking anywhere else.**

**The workspace's own file permissions are a trap on Windows, measured here 2026-10-02.** A
confined session can end up unable to write anywhere below the workspace root: the harness reports
`SetNamedSecurityInfoW failed (Win32 5): grantWrite(C:\…\DEEP_SCADA_DARBOX)` on its first command,
or commands fail afterwards with `git fetch` → `cannot open '.git/FETCH_HEAD': Permission denied`
and `dotnet build src/Core` → `Access to the path '…\src\Core\obj' is denied`, while plain file
reads and edits still work. It is a permissions problem on the *machine*, not in the repository:
the DSH skill `diagnose-windows-sandbox-acl` repairs the rights that are missing (one command, with
a backup and a rollback command written beside it), and where the directory it repairs is still not
enough, the session has to run with **full access** for the work. Symptoms, cause and the two
recovery commands of the first repair are in this session's record on the work machine
(`C:\GitProekti\dsh-acl-recovery\`); nothing about it is committed, because nothing about it
belongs to the project.

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
IPv4 and another on IPv6); here it has happened on 5432. **Corrected 2026-10-01: the suite no
longer has to fight over the port.** The test projects take `SCADA_TEST_DB_PORT` as well as
`SCADA_TEST_DB_HOST` (`tests/Persistence.Tests/TestDatabase.cs`,
`tests/Gateway.Tests/Hosting/TestDatabases.cs`), and the development Compose file publishes
`${SCADA_DB_PORT:-5432}`, so the container can sit on 5433 beside the native server — measured
that day at **401 passed, 0 skipped, 0 failed** across seven projects, with the native service
running throughout. **The native service was not stopped**: it is a machine-level service that
was there before this session, and stopping it is the operator's call, not a side effect of
writing a handover.

**2. Docker *is* on the Windows PATH now, and WSL is refused. *(Corrected 2026-10-01; this
paragraph said the opposite.)*** `docker` resolves to Rancher Desktop (`docker version` →
`29.8.0`, `docker compose version` → `v5.3.1`), while `wsl docker` answers `Access is denied
(Wsl/E_ACCESSDENIED)`. What still holds from the original note: a **sandboxed** process cannot
open the Docker pipe, so `RequiresDockerFact` and `RequiresDockerComposeFact` tests report as
skipped from a confined run and run normally outside one. `deploy/README.md` assumes Docker
Desktop is on the PATH, which on this machine means running the guide from a shell that has it.

**3. LF and CRLF.** Committed blobs are LF and the working tree is CRLF, and a root
`.gitattributes` (added 2026-09-27, `527654a`) now has the two gits agree about what counts as
modified — this paragraph used to say the repository had none. What remains: **use Windows git**
for `status`, `diff`, `log` and `commit`, and shell scripts with CRLF break `bash`, which is how
`deploy/cloud/certs.sh` failed in the Phase 7 walk (`set -euo pipefail` with a trailing CR parses
as an unknown command name). That defect is fixed in this repository's PR #3, and
`deploy/cloud/.gitattributes` exists for that subdirectory, but the general hazard stands: strip
the CRs before running a script with WSL `bash` (`tr -d '\r' < script.sh > /tmp/script.sh`),
which is also needed for `deploy/build-images.sh` line 27 on a fresh checkout.

**4. `deploy/.env` and `deploy/cloud/.env` exist locally and are correctly ignored.** No
non-example `.env` is tracked (`git ls-files` lists only the three `.env.example` files), so
secrets are not in the history — but they *are* on this machine, and the stacks running from
`deploy/` use them. The cloud stack was also started from a second copy of the repository at
`/root/deep-scada-darbox` inside WSL (`com.docker.compose.project.config_files` on
`scada-timescaledb`), which is worth knowing before assuming a running container reflects the
working tree you are looking at.


The defects that were already found and fixed are listed in [`HANDOVER-archive.md`](HANDOVER-archive.md).
### Genuinely open limitations

- **No CI.** There is no `.github/` directory and no other pipeline: every build, migration and
  test run is done by hand, and the "gate met" claims rest on a recorded hand walk. Adding a
  pipeline is not scoped by any phase.
- **The edge's device/tag list is no longer copied by hand, and the answer is built.**
  *(Corrected 2026-10-01: this bullet said the list was still typed by hand and that the answer was
  not built. Both had stopped being true — `Edge:Devices` is gone from
  `src/EdgeAgent/EdgeOptions.cs`, and the cloud derives each edge's list and publishes it.)* What is
  still unproven is the walk: no run has yet had a real broker and a real edge accept a derived
  configuration. See `docs/roadmap/open-work.md`.
- **A test run without a database is not the same evidence as one with it.** The archive's suite
  table and `docs/roadmap/open-work.md` §2.3 show the shape: with no database reachable, **134 of
  394** .NET tests reported as skipped on 2026-10-01 (it was 133 of 327 on 2026-09-27, and the
  counts move as tests are added). Treat any "all green" claim as conditional on which of the
  three prerequisites (database, `docker version`, `docker compose version`) were actually
  available, and say which. **The baseline is `open-work.md` §2.4–§2.5, 2026-10-02: 447 passed and
  0 skipped** across seven projects with `SCADA_TEST_DB_PORT=5433`, plus the client's 63 — after
  ADR-0019 §8, ADR-0020 and ADR-0021 added their tests, and the first runs in which nothing was
  skipped.
  **Read §2.5 before trusting a red run.** The suite was *not* green when §2.4 was first written:
  eight whole-solution runs produced one or two failures each, never the same pair twice, every one
  of them passing in isolation. Two causes were found — `Command Timeout=10` on the test helpers'
  administrative connection making the client give up on a slow `CREATE`/`DROP DATABASE`, and three
  tests in `EdgeDriverDeclarationsTests` waiting for the declaration rather than for the audit row
  appended after it — and after fixing both, seven consecutive whole-solution runs were green.
  A whole-solution run on this machine **is** green now; what is not fixed is the design those two
  causes came from, and §2.5 says which parts remain.
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


The exact commands behind the measurements are in [`HANDOVER-archive.md`](HANDOVER-archive.md).

## 6. Where the 2026-10-02 and 2026-10-03 sessions stopped, and what to do next

Read this first. It is the state of the tree after the 2026-10-03 session's last commit, and
everything below it in this file is still true unless this section says otherwise.

**Where the 2026-10-03 session stopped.** Nothing is in flight and nothing is half-done: the work is
committed, pushed, and `main` is level with `origin/main`. The session produced ADR-0023 and then
three rounds of tests closing the gaps it had left — `ea26cea` (the edge's executor, against a real
Modbus slave), `231d459` (the write ACL and the retain rule, against a real Mosquitto), and a third
(the uplink taking a write off a real broker), each finding or proving something the round before it
had not. `bec4120` and `5371bfc` bring this file and `CLAUDE.md` up to date; the run that closed the
session reported **501 passed, 0 skipped**. The tree is clean; the only thing left running that this
work started is the test database on **5433**, which now carries `--restart unless-stopped` so a
stopped container cannot make a later run look green when it is not.

**The one thing to do next is unchanged and is written out in "What to do next, in order" below:**
walk ADR-0023's write path end to end. Every joint is now executed by something — the API's answer,
the router's matching, the payload, the executor against a real device, the uplink off a real broker,
the retain rule and the ACL against a real broker — and no run has crossed all of them at once. That
last sentence is the whole reason the walk is still owed, and it needs the hardware (§1.3).

**The one thing to do next is unchanged and is written out in "What to do next, in order" below:**
walk ADR-0023's write path end to end. Every joint is tested and no run has crossed all of them at
once, and it needs the hardware (§1.3).

### What the 2026-10-02 session closed

| What | Outcome |
|---|---|
| **Phase 7's gate, on two hosts** | `open-work.md` §1.1 and §1.2 — a real network boundary and two clocks. Recorded in [`phase-7-manual-gate.md`](docs/roadmap/phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02). |
| **The suite was not green** | §2.5: two load-induced flakes, diagnosed and fixed. 8 of 8 runs red before, 10 of 10 green after. One cause is a proven race, the other is read off the failure and recorded as such. |
| **ADR-0020** | A device with no tags is omitted from an edge's configuration. Decided, implemented, mutation-verified. |
| **ADR-0021** | An edge reports the devices it cannot read, on its declaration (**payload version 2**, the project's first). Decided, implemented, mutation-verified, shown in the client. |
| **ADR-0022** | An edge's link device is derived, is not overridable, and the edge names its two limits. Decided, implemented, mutation-verified, form updated. |

### What the 2026-10-03 session closed

| What | Outcome |
|---|---|
| **ADR-0023** | A tag write to a device an edge reads is **routed to that edge** over the link. Decided, implemented, mutation-verified five times. |
| **Its whole path, one joint at a time** | After the change, the halves that had only been *compiled* were executed: the edge's executor against a real Modbus slave, the uplink taking a write off a real broker, the retain rule and the ACL against a real Mosquitto. That found a defect. |

**That one is different in kind, and it is the thing to understand before touching the write path.**
Every other payload on the link carries a measurement or a fact backwards, or configuration forwards.
This is the first that travels from the cloud to a plant **to ask for something**, so it is the first
thing in this project that lets the cloud change a plant. Three guardrails hold it, and each has a
reason that must be read before one is relaxed (ADR-0023 §3–§4):

- the write topic is **not retained** — a write an edge receives the moment it reconnects is a
  command to change a plant after the reason for it has passed;
- a write is **not buffered**, for the same reason: a late sample is still true of its moment, and a
  late command is not;
- a write is **never reported as done before it is** — five seconds or a **504 saying "not
  confirmed"**, because 202 would be the untrue answer.

A reply is matched to its call by a **`writeId`**, which is the whole mechanism: several writes may
be in flight to one edge and an edge may answer out of order, so a reply naming an id that is not in
flight is ignored rather than attributed. The journal keeps the three outcomes apart —
`tag.write`, `tag.write_failed` (with the edge's own reason), and `tag.write_unconfirmed` — and
`tag.write_refused` when a deployment has turned writing over the link off
(`EdgeProvisioning:WritesEnabled`, on by default).

### The defect that only running the edge's half found

`EdgeWriteExecutor` returned `WriteResultResult.Refused` for every way it could fail. A refusal in
that type means **the message could not be read** — it carries no write id and a null reason — so the
uplink treated a perfectly well-formed request as unreadable, logged, and **sent nothing back**. The
cloud would have waited out its five seconds and told an operator "not confirmed" about a write the
edge knew had failed. That is precisely the untruth ADR-0023 §4 exists to prevent, and it came from
using a type for the wrong thing.

Every failure the executor can produce is a request **understood and not carried out**, which is a
*result*: the id comes back with the edge's reason. It is now one `Failed` helper, the method
documents that it never returns `Refused`, and restoring the defect fails four named tests while
three controls stay green.

**The lesson is the one this project keeps relearning: compiling is not running.** The cloud's half
had a broker, the wire format had a test, and the edge's half had neither — and that was where the
bug was.

**Test baseline: 502 .NET across seven projects, 0 skipped, plus the client's 63.** Measured with
`SCADA_TEST_DB_PORT=5433` (§2.4–§2.5). One flake was seen and is recorded there as a sighting, not a
diagnosis — its name was not captured and the project passed twice afterwards.

**The environment that produced it** is written down in §5, under "Left running on this machine
after that walk". Short version: the `scada-darbox-cloud` stack and a Modbus simulator are up, a
test database sits on **5433**, and `C:\Users\darko\scada-certs` is the active CA. The WSL copy of
this repository and its separate CA are **gone** — do not go looking for them, and if a container
reports a certificate it should trust, check which CA signed the file it was handed.

### What ADR-0022 changed that a reader of older notes will get wrong

- **An operator no longer creates an edge's link device, and the API ignores a `linkDeviceId` it is
  sent.** `LinkDeviceProvisioner` writes it from the edge's name and the deployment's settings.
- **The "a link has to be pushing, and still configured" refusal is gone.** It is replaced by an
  invariant: the only thing that sets a link derives an MQTT device.
- **`Edge.LinkStaleness` and `Edge.LinkSessionExpiry` are new**, stored as
  `link_staleness_seconds` (60) and `link_session_expiry_hours` (720) by migration **0016**, and they
  are the only thing about a link an operator chooses.
- **Multiple edges means multiple link devices on purpose.** One shared subscription would give one
  staleness limit to every edge, so one silent plant would mark every other plant's tags Bad.

### What to do next, in order

1. **Walk ADR-0023's write path end to end.** `open-work.md` §2.0 records four things written,
   implemented and tested that have never been through a real link, and **the write is the one to do
   first**: it is the only one where being wrong means a plant was changed or an operator was told it
   was. Every *joint* is now executed by something — the API's answer, the router's matching, the
   payload, the executor against a real Modbus slave, the uplink taking a message off a real broker,
   the retain rule and the ACL against a real Mosquitto — and **no run has crossed all of them at
   once**. That is what the walk is for: a browser asking, the router publishing, a real edge taking
   it off a real broker, a real device changing, and the result reaching the operator's screen.
   **And one path that was believed bounded is not.** A device that accepts a connection and then
   says nothing *was* executed, and it came back as a failure with a reason after **about twenty
   seconds** — the executor's ten-second deadline and `ModbusTcpDriver`'s five-second socket timeout
   both exist and **neither stopped it**, because NModbus does not honour the cancellation token
   during a read and the failure surfaced as a transport error instead. ADR-0023's honesty rule
   holds, so an operator is told it failed rather than that it succeeded; what does not hold is the
   bound this path was believed to have. Whether it should is a decision nobody has made, and it is
   the one thing in this path that is a question rather than a gap.
2. **Then the other three items in `open-work.md` §2.0** — ADR-0020's omission, ADR-0021's
   reporting and its payload version (two builds of different ages on one link), and ADR-0022's
   derived link actually subscribing. All want a broker and an edge, so they belong to the **same
   two hosts** the gate was walked on, and the recipe is the gate record's two-host appendix.
3. **`open-work.md` §3 has eight decisions left.** None is as consequential as the last four were.
4. **`open-work.md` §1.3 — a real arm64 board — is untouched.** It is the one item in §1 that no
   amount of software closes; it needs the hardware.

### How to work here, as those sessions learned it

- **The design conversation decides, this session implements.** When a decision is missing, write
  the ADR first and mark it `Proposed` if it is asking rather than stating. ADR-0022 came back with
  its decision 6 tightened beyond what the proposal had; ADR-0023 came back with a decision 8 added
  (a deployment may refuse writes over the link) that the proposal had raised as a question.
- **A fake that differs from the real repository hides defects.** `FakeCatalogue.UpdateAsync` was
  dropping the two new link settings and a test caught it only because it asserted on them.
- **Verify by mutation, and say what the mutation proved.** ADR-0023's four are the model, and two
  of them taught something. With a reply accepted without matching its id, the out-of-order test
  fails and its six controls stay green; with a timeout reported as written, the honesty test fails
  and its six stay green; removing the Gateway's ACL permission fails three broker tests with eleven
  controls green. And **a test that passes under a mutation is not a test**: the first version of the
  "an edge cannot read another edge's write requests" test asserted only that nothing arrived, which
  an ACL refusing everything satisfies just as well — it passed under a mutation that removed the
  rule. It needed a positive control (read your own, then fail to read another's) before it meant
  anything. ADR-0021's edge-side report had the same shape and was fixed the same way.
- **Don't write a test whose name claims more than its assertions.** A broker test for "the write is
  not retained" was drafted that published *with* the retain flag and watched the broker honour it.
  It would have passed while proving nothing about this product, and its name would have said
  otherwise; it was replaced by two tests of the real guarantee — the publisher's write is not
  retained, and a configuration from the same publisher is.
- **Rebuild after restoring a mutation**, and check the restored file rather than trusting the edit.
  A mutation that does not compile is not a failed mutation; it is a broken experiment. And a
  `Select-String` check for "is it restored" can match a *comment* about the mutation rather than the
  code — read the line, not the match.
- **When a test asserts behaviour a new ADR removes, rewrite the test to assert the new behaviour
  and say what it replaced.** `EdgeApiTests` and `EdgeOwnedDeviceWriteTests` both keep their old
  test's story in a comment for exactly that reason.
- **Run the database-backed suite before believing a count.** A stopped test container produced
  `Passed: 5, Skipped: 70` in one project during this work, which looks like a green run and is not
  one. It now carries `--restart unless-stopped`.
