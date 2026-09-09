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

**Phase 0 and Phase 1 are both complete and merged to `main`.** Phase 1
(core skeleton) shipped Core, Persistence.TimescaleDb, the Modbus TCP
driver module, the Gateway (Web API + SignalR), schema migrations via DbUp
(ADR-0007), the Angular web client, a Modbus simulator, and 36 passing
tests. Its test gate is confirmed (see `phase-plan.md`'s Phase 1 status
note) against TimescaleDB and a live simulator, including the case of a
device going offline (Bad quality, not a fabricated value).

**Phase 2 (tag browsing and device management) is now starting.** Scope per
`phase-plan.md`: the hierarchical browse tree UI (ADR-0001), device
configuration through the UI — add/edit a Device and its connection, no
code required to add a device — dimensioned unit display (ADR-0005), and a
basic historical trend chart for a tag. Test gate: a new device and its
tags can be added and browsed entirely through the UI, with a working
trend chart. Don't begin Phase 3 or later scope until this gate is met.

Phase 2 needs config CRUD through the UI, which is exactly the trigger
CLAUDE.md already flagged for revisiting the "no ORM" choice from Phase 1
(see "When Phase 1 (or any phase) begins" below) — raise that as an open
question here rather than picking EF Core/Dapper/raw SQL unilaterally.

## When Phase 1 (or any phase) begins

- Build only what that phase's entry in `docs/roadmap/phase-plan.md` scopes
  — not more. A phase's test gate is the definition of done; don't move to
  the next phase's scope before the current gate is met and confirmed.
- Follow ADR-0006 for every technology choice. Adding a dependency outside
  that table needs a new or amended ADR first, not a unilateral substitution
  because something seemed easier.
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
  doesn't name one, so Phase 1 uses plain Npgsql/SQL. Revisit this
  explicitly (new ADR) when Phase 2 needs config CRUD through the UI.

## Solution layout

```
src/
  Core/                     tag engine, driver framework contracts, historian
                            abstraction, alarm engine, users/roles/auth — no
                            ASP.NET host, no concrete persistence dependency
  Persistence.TimescaleDb/  concrete historian implementation (plain Npgsql/SQL,
                            hypertable) behind Core's storage abstraction
  Gateway/                  Web API host, SignalR hub, the scan service wiring
                            drivers + tag engine + historian together
  Modules/
    Drivers.Modbus/          Modbus TCP driver (Phase 1)
    Drivers.OpcUa/           (Phase 4)
    Drivers.Mqtt/            (Phase 4)
  EdgeAgent/                .NET Native AOT edge process (cloud topology,
                            not needed until that topology is built)
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
