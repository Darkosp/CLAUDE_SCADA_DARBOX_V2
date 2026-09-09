# ADR-0008 — Dapper for configuration-table data access

**Status:** Accepted
**Date:** 2026-09-09

## Context

Phase 2 needs config CRUD through the UI (add/edit a Device and its
connection, no code required) — the trigger CLAUDE.md already flagged for
revisiting the "no ORM" choice from Phase 1. ADR-0006 named no ORM for
Phase 1's raw-SQL approach; ADR-0007 then gave schema ownership to DbUp,
numbered SQL scripts applied and journaled at startup. Phase 2's CRUD
surface is small: Tenant, Site, Device, Tag — four configuration tables,
each with a handful of columns, none of them the historian's high-volume
time-series data.

Two data-access approaches were considered against that surface.

**EF Core** would give the CRUD layer change tracking, LINQ queries, and
generated SQL, but it comes with consequences that don't fit this
codebase:

- ADR-0007 already gave DbUp ownership of the schema as the single source
  of truth. EF Core migrations are a second tool with an opinion about the
  same schema; keeping EF's model and DbUp's scripts from drifting apart
  is exactly the kind of two-tools-one-job problem ADR-0007 exists to
  avoid.
- EF entities and `DbContext` usage would pull `Microsoft.EntityFrameworkCore`
  into whatever project defines the config entities. Per ADR-0002, Core
  has no concrete persistence dependency — that's precisely why Gateway
  and Persistence.TimescaleDb are separate projects from Core in the first
  place. Routing config entities through EF re-creates the dependency
  ADR-0002's project split was built to prevent.
- The historian itself (Persistence.TimescaleDb) has to stay on plain
  Npgsql regardless — binary `COPY` for ingest and hypertable-specific SQL
  aren't things EF Core does. Adopting EF Core for four config tables
  would still leave the historian on raw Npgsql, so the codebase would run
  two data-access approaches side by side without EF actually replacing
  the raw-SQL path anywhere that matters.

**Dapper** stays a thin extension of what Phase 1 already does: it maps
query results onto plain objects (parameterized SQL still written by
hand, DbUp still owns schema, no change tracking, no migrations feature to
collide with ADR-0007) and adds no dependency on EF Core, so Core's
persistence-free boundary from ADR-0002 is untouched.

Dapper does not remove all boilerplate. It replaces fragile positional
column-to-property mapping with named mapping, but a domain-shaped
projection — for example materializing a Tag's `UnitOfMeasure` (dimension
+ SI factor + symbol + display name, ADR-0005) from four flat columns
back into that value object — is still written by hand in each query that
needs it. Dapper's benefit here is narrower than "no more boilerplate": it
removes one specific failure mode (a column reorder silently shifting
values into the wrong property) without removing the need to hand-write
domain projections.

## Decision

Adopt **Dapper** for configuration-table data access (Tenant, Site,
Device, Tag CRUD) added in Phase 2. Schema for these tables continues to
be owned entirely by DbUp (ADR-0007) — Dapper only queries and maps
against a schema it never defines or migrates. The historian
(Persistence.TimescaleDb) is unaffected by this decision and stays on
plain Npgsql, per ADR-0007's rationale about `COPY` and hypertable-specific
SQL. Dapper is added as a dependency of the Gateway/Persistence layer that
implements Core's repository contracts — never a dependency of Core
itself, preserving ADR-0002's boundary.

## Consequences

Config CRUD gets named result-mapping instead of hand-rolled
`IDataReader` column-index reads, at the cost of one small added
dependency (Dapper). Hand-written domain projections (e.g. `UnitOfMeasure`
from ADR-0005) remain hand-written — Dapper narrows one failure mode, it
doesn't eliminate the projection code. Schema for the four config tables
continues to live exclusively in DbUp migration scripts; there is no
second tool (an EF model, a second migrations mechanism) with an opinion
about that schema to drift out of sync.

## Verified in review by

- No `Microsoft.EntityFrameworkCore*` package reference appears anywhere
  in the solution, and Core has no reference to Dapper or any concrete
  ADO.NET provider.
- Every config-table schema change is still a new numbered DbUp script
  under `migrations/` (ADR-0007) — Dapper usage never introduces a
  parallel migrations mechanism.
- Historian code paths (Persistence.TimescaleDb) are untouched by this
  ADR and continue to use plain Npgsql directly.
