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

As of this writing: **Phase 0 is complete** (architecture and ADRs are in
place). **Phase 1 has not started.** Do not begin writing application code —
including scaffolding a solution, adding NuGet/npm packages, or creating
`src/`/`tests/` content — until explicitly told a phase has begun. Until
then, this repo's working scope is limited to committing and pushing the
planning documents produced in the accompanying design conversation
(README, ADRs, architecture, roadmap, this file) — plain git housekeeping,
not implementation or design work.

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
  reflection-based dynamic plugin loading.
- Tag identity is the stable ID from ADR-0001, never the display path.
  Historian and alarm code must reference tags by ID.
- Every tag value carries the typed union, source timestamp, and quality
  fields from ADR-0003 — don't simplify a driver to always report "Good" or
  drop the source timestamp for convenience.
- Every Site-scoped entity is transitively Tenant-scoped (ADR-0004), even
  though a given deployment has exactly one Tenant row.
- Units are dimension + SI factor (ADR-0005) — never a bare unit string in
  the data model.

## Solution layout (once Phase 1 starts)

```
src/
  Core/               tag engine, driver framework contracts, historian
                       abstraction, alarm engine, users/roles/auth, Web API, SignalR
  Modules/
    Drivers.Modbus/    (Phase 1)
    Drivers.OpcUa/      (Phase 4)
    Drivers.Mqtt/       (Phase 4)
  EdgeAgent/          .NET Native AOT edge process (cloud topology)
  Web/                Angular application
tests/
docs/                 (already established — architecture, decisions, roadmap)
```

Adjust only with a reason tied back to an ADR; don't restructure for taste.

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
PR/review process starts once Phase 1 begins and there is real application
code to review.

## Commits

Commit messages should reference the relevant ADR or phase where relevant,
e.g. `docs: add ADR-0007 — <title>` or `feat(phase-1): tag engine schema
(ADR-0001, ADR-0003)`. Keep documentation commits and implementation commits
separate.
