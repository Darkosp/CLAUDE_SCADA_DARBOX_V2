# Architecture Decision Records

Binding decisions for SCADA_DARBOX. Every pull request is reviewed against them.
An ADR is superseded, never edited into a different decision — to change one,
add a new ADR that supersedes it and state why the original no longer holds.

The one exception: an Accepted ADR may be revised in place **while nothing has
been implemented against it yet**, provided the revision and its reason are
recorded inside that ADR rather than silently replacing the old text. Once code
depends on a decision, it is superseded, never revised. Both shapes exist here
and are worth contrasting: ADR-0011's token mechanism was revised in place
(decided and rewritten the same day, no code had been written against it), while
ADR-0007's migration-execution clause was superseded by ADR-0012 (it had shipped
in Phase 1 and everything since ran on it). A superseding ADR may also supersede
a single clause rather than a whole decision, as ADR-0012 does — say so in its
header, and mark the clause in the original.

| ADR | Decision |
|---|---|
| [0001](0001-tag-identity-and-hierarchy.md) | Tags have a stable ID; the hierarchical path is a mutable display label |
| [0002](0002-core-module-boundary.md) | Core is domain-neutral; modules are compile-time composed, not runtime plugins |
| [0003](0003-tag-value-model.md) | Tag values are a typed union (Numeric/Boolean/Text/Discrete) with source timestamp and quality |
| [0004](0004-tenant-scoping.md) | Tenant scoping exists from day one, above Site, even in single-tenant deployments |
| [0005](0005-dimensioned-units.md) | Units are a dimension + SI factor, never a free-text label |
| [0006](0006-technology-stack.md) | .NET + Angular + PostgreSQL/TimescaleDB + SignalR + OPC UA/Modbus/MQTT + Docker |
| [0007](0007-schema-migrations.md) | Schema migrations via DbUp — numbered SQL scripts, no manual ALTERs |
| [0008](0008-dapper-for-config-tables.md) | Dapper for configuration-table (Tenant/Site/Device/Tag) data access, schema still owned by DbUp |
| [0009](0009-soft-delete-via-active-view.md) | Soft delete for Folder/Device/Tag via a database view, not app-level filtering |
| [0010](0010-udt-live-reference-semantics.md) | UDTs are a live-reference type with materialized per-instance tags; template edits propagate immediately |
| [0011](0011-permissions-model.md) | Opaque session token, roles/Site-scope resolved server-side per request; Site-scoped Operator/Viewer, tenant-wide Admin |
| [0012](0012-migrations-run-outside-the-gateway.md) | Migrations run as a separate privileged step; the Gateway refuses to start unless the schema matches its build exactly (supersedes ADR-0007's execution clause) |
| [0013](0013-alarm-journal.md) | Alarms persist as an append-only event journal, with the live list derived from it; shelving is time-bounded |

## Template

```markdown
# ADR-NNNN — Title

**Status:** Proposed | Accepted | Superseded by ADR-NNNN
**Date:** YYYY-MM-DD

## Context
What forces are at play, and what happens if we get this wrong.

## Decision
The decision, stated so that it can be checked.

## Consequences
What this costs us and what it buys us.

## Verified in review by
Concrete, checkable criteria a reviewer applies to a pull request.
```
