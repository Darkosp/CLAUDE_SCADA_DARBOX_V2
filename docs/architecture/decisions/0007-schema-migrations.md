# ADR-0007 — Schema migrations via DbUp

**Status:** Accepted
**Date:** 2026-09-09

## Context

Fixing Phase 1's quality-value defect required a real schema change
(`value_kind` from `NOT NULL` to nullable). With no migration framework in
place, it was applied by hand directly against the dev database — acceptable
once, as a one-off during early development, but not a practice that can
continue once real historian data exists across deployed instances: a
manual `ALTER` is not repeatable, not reviewable as a diff, and not
something a fresh environment (a new developer machine, a new customer
deployment) can replay.

ADR-0006 already settled on no ORM — Phase 1 uses plain Npgsql/SQL. A
migration tool needs to fit that choice, not reintroduce the weight of a
full ORM just to get versioned schema changes.

## Decision

Adopt **DbUp** for schema migrations: plain, numbered `.sql` scripts under
a `migrations/` folder, applied in order, tracked in DbUp's own journal
table so re-running is a no-op for scripts already applied. This is a
direct extension of the existing raw-SQL approach, not a new paradigm.

Migrations run automatically as a startup step in the Gateway host, before
it begins serving requests. Each deployment is currently single-instance
per tenant (ADR-0004's single-tenant model), so there is no concern yet
about multiple instances racing to apply the same migration concurrently;
revisit this specific point if that assumption changes (e.g. a
horizontally-scaled Gateway in a later phase).

A manual `ALTER` (or any other hand-applied schema change) against a
running database is no longer an acceptable way to evolve schema —
every schema change is a new, numbered migration script from now on.

## Consequences

Schema changes become reviewable, repeatable, and safe to replay on a
fresh database (a new developer's machine, a new customer instance). The
cost is one small added dependency (DbUp itself, no ORM) and a convention
to follow: number scripts in order, never edit an already-applied script
— add a new one to change course, the same discipline already used for
ADRs.

## Verified in review by

- Any PR that changes core or historian schema includes a new numbered
  migration script under `migrations/`, never a manual `ALTER` instruction
  in a commit message or a runbook.
- DbUp's journal table exists, and migrations apply cleanly, in order,
  starting from a completely fresh database.

## Implementation note (2026-09-09)

Implemented against a fresh database (journal created, both scripts applied
in order, hypertable with compression off) and, more importantly, against
the existing dev database that already carried the manual `ALTER` from the
Phase 1 fix but no journal — the harder case this ADR exists for. Both
scripts recorded as applied with no data loss (2188 samples, 2 tags, the
tenant row all unchanged), and a second run correctly applied nothing.
`DatabaseSchema.cs` (the prior hand-written schema setup) was removed;
migration scripts are embedded as assembly resources so a deployment can
never be separated from the migrations it depends on, while still living
under `migrations/` in the repo for review. A failed migration throws
rather than letting the host start serving against a schema in an unknown
state.
