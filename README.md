# SCADA_DARBOX

A modular, web-based SCADA platform, built on .NET and Angular.

## Status

**Phase 0 — Concept and architecture.** No application code yet by design.
This repository is a fresh start: the concept, architecture and every binding
decision are being defined here, from scratch, before implementation begins.

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
  briefings/         Session hand-off notes and planning summaries
  architecture/       Architecture overview and decisions
    decisions/        Architecture Decision Records (ADRs)
  roadmap/            Phased delivery plan
src/                  Application source (from the implementation phase)
tests/                Automated tests (from the implementation phase)
```

## Licence

Proprietary. All rights reserved.
