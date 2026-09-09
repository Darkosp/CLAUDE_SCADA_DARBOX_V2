# Architecture Decision Records

Binding decisions for SCADA_DARBOX. Every pull request is reviewed against them.
An ADR is superseded, never edited into a different decision — to change one,
add a new ADR that supersedes it and state why the original no longer holds.

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
