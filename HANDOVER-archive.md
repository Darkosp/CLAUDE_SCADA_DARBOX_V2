# Handover archive - SCADA_DARBOX

This is the historical half of `HANDOVER.md`, split out on 2026-09-28 so that `HANDOVER.md` stays
under the CLI's tool-output masking threshold (8000 tokens, roughly 32 000 characters). Nothing here
was deleted or rewritten - it is the same text, moved.

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

### ADR-0019 — the assignment half: which devices an edge reads (PR #6, merged)

ADR-0019 decided that the cloud authors each edge's configuration and publishes it onto the link
the edge already holds. The half that has to exist first is merged: `Edge` is an entity of the
tenant rather than of a Site — its name is the identity in its certificate and the segment the
broker carries its topics under (ADR-0017), unique deployment-wide (ADR-0015) — a device is
assigned to one by an ordinary edit of that device, and the Gateway stops polling a device an
edge reads and refuses a write to it by name (ADR-0003) instead of reporting a device error that
never happened. `/api/edges` is Admin-only and every write is journalled (ADR-0011); the Edges
screen and the edge picker on the device form are in the client.

Evidence: 353 .NET tests with a docker daemon up — 346 passed / 7 skipped / 0 failed with it down,
the 7 being `BrokerConfigurationTests`, which need `docker version` — and 53 client tests; the
create-path guard and the link rule were watched to fail, one mutation at a time, in PR #6. What
is **not** built is the provisioning half: the Gateway publishing `{prefix}/{edgeId}/config`
retained and versioned, one MQTT ingestion source per edge, the EdgeAgent subscribing and holding
a durable last-accepted configuration, and the deploy ACL widening that lets `scada-gateway` write
`scada/edge/+/config`. Until those land the hand-written `deploy/edge/edge.example.json` stands.

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






