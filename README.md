# SCADA_DARBOX

A modular, web-based SCADA platform, built on .NET and Angular.

## Status

**Phase 7 — Cloud topology, in progress; Phase 8 — Operator screens, scoped
and largely built.** The platform exists and runs:
Core, the Gateway and its browser client, PostgreSQL/TimescaleDB, the
on-premises and cloud Compose topologies, and Modbus, OPC UA and MQTT
drivers. Phases 0–6.5 are complete and merged to `main`, and so are Phase
7's first five steps — the pushing contract, the MQTT driver, the edge
agent, cloud ingestion, and the broker over TLS — and the driver logging
beside them. Its hand walk, with the link cut for real, is **walked and
recorded**: [the Phase 7 gate record](docs/roadmap/phase-7-manual-gate.md) has
the numbers — the history holding the readings measured during the outage with
the edge's own timestamps, the readings a lowered buffer bound dropped reported
as one journal entry, and an outage nothing was measured in adding no row. What
Phase 7 still owes is a walk on plant hardware: `linux-arm64` is built and run
under emulation, not on a board. **ADR-0019 is implemented on both sides** — the
cloud derives each edge's devices and publishes them on the link the edge already
holds — but it has not had a walk of its own.

**Phase 8 gives the platform operator screens**, and a screen here is
configuration rather than code (ADR-0024): an author builds one in the browser
from a closed set of six component kinds, the sixth being an equipment symbol
that derives a named state from its tag (ADR-0027). An Operator can write a tag
from a screen (ADR-0026), the client has a light and a dark theme, and both
halves have been walked by a person — on 2026-10-05 and again on 2026-10-06,
recorded in [the gate record](docs/roadmap/phase-8-manual-gate.md) and
[the 2026-10-06 walk](docs/roadmap/walk-2026-10-06.md).

[`docs/roadmap/open-work.md`](docs/roadmap/open-work.md) registers all of that and
every other unfinished or unverified item, with what each one waits for.
`CLAUDE.md` carries the current
state; `docs/roadmap/phase-plan.md` holds the plan and each phase's status.

Prior work in sibling repositories/documents on this machine (e.g. `NewScada`,
`SCADA-BRIEFING.md`, the booster-pump specification) is **not** a foundation
for this project. Isolated ideas or patterns from that material may be reused
here only where explicitly noted as such in this repo's own docs.

## Start here

| Document | What it covers |
|---|---|
| [Phase 0 Architecture](docs/architecture/phase-0-architecture.md) | System components, deployment topologies, data flow, technology stack |
| [Architecture Decisions](docs/architecture/decisions/README.md) | Binding ADRs every change is reviewed against |
| [Phase Plan](docs/roadmap/phase-plan.md) | Delivery phases, each with an explicit scope and test gate |
| [CLAUDE.md](CLAUDE.md) | Working instructions for Claude Code sessions in this repo |

## How this repo is built

This project is designed collaboratively in conversation (concept, architecture,
planning) and implemented by Claude Code from the resulting documents. The two
stay in a continuous loop for the life of the project — not a one-time handoff.

Every substantive planning artifact (text, diagrams, decisions) is saved into
this repository as it's produced, so there is always a durable trace to return
to.

## Repository layout

```
docs/
  architecture/       Architecture overview and decisions
    decisions/        Architecture Decision Records (ADRs)
  roadmap/            Phased delivery plan
deploy/               Compose topologies and operator guides
  edge/               The edge machine: agent, drivers, local buffer
  cloud/              The cloud: Gateway, database, broker and its TLS material
src/                  Application source — Core, Gateway, EdgeAgent, client, modules
tests/                Automated tests, one project per unit under test
tools/                Modbus and OPC UA simulators, used by tests and by hand
```

## Licence

Proprietary. All rights reserved.
