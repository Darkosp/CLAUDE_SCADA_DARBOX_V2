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

**A later ADR that closes a gap an earlier one recorded as open amends that
sentence in the same pull request.** The sentence is a claim about the present
tense, and once the gap is closed it is a false one, inside a document every pull
request is reviewed against: a later session reads "still in-memory only, to close
before Phase 6" and goes looking for work that is already done, or worse, distrusts
the journal it was told does not exist. This is not the in-place revision the
paragraph above restricts — no decision changes, only a statement that stopped
being true. The first instance found was [ADR-0011](0011-permissions-model.md),
whose acknowledgement section said alarm *state* remained in-memory only — true when
it was written on 2026-09-11, false from 2026-09-18 when
[ADR-0013](0013-alarm-journal.md) was accepted, and left standing for nine days;
when opening an ADR, search the earlier ones for the gap it closes and fix what
they say about it.

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
| [0014](0014-one-migrator-at-a-time.md) | One migrator run at a time, enforced by a database advisory lock; a build with zero migration scripts refuses to run |
| [0015](0015-names-unique-within-their-parent.md) | A name is unique within its parent, among live rows, ignoring case; duplicates are renamed on upgrade, not refused |
| [0016](0016-push-capable-driver-contract.md) | A driver declares itself polled or pushing; a pushing tag goes Bad on silence rather than holding a cached value |
| [0017](0017-edge-to-cloud-link.md) | Edge-to-cloud: our own payload over Mosquitto with TLS per edge; the edge buffers on disk, drops oldest and records the loss; alarms stay in the cloud |
| [0018](0018-edge-agent-runtime-and-buffer.md) | The edge agent runs self-contained on the CLR (Native AOT breaks OPC UA) and buffers in SQLite (amends ADR-0006) |
| [0019](0019-edge-configuration-provisioning.md) | How an edge is configured: the cloud is the source of truth, delivered over the link it already has |
| [0020](0020-devices-with-no-tags.md) | A device with no tags is omitted from an edge's configuration — the derivation cannot produce what the reader refuses |
| [0021](0021-edge-reports-what-it-cannot-read.md) | An edge says which assigned devices it cannot read, on its driver declaration — the project's first payload version bump |
| [0022](0022-derived-link-device.md) | An edge's link device is derived from the edge, is not overridable, and the edge names its staleness limit |
| [0023](0023-routing-writes-to-an-edge.md) | A tag write is routed to the edge that reads the device, is never queued or retained, and a deployment may turn it off |
| [0024](0024-a-screen-is-configuration.md) | An operator screen is configuration and not code, its component set is closed, and every component that reads a tag shows that tag's quality |
| [0025](0025-an-alarm-waits-before-it-announces-itself.md) | An alarm waits before it announces itself, and a deadband shifts where it clears but never where it raises |
| [0026](0026-operating-from-a-screen.md) | One component writes and nothing else does, the server decides whether the control is offered, and the write is never held or reported done before it is |
| [0027](0027-a-symbol-derives-a-state.md) | A symbol is a component kind that derives a named state from its tag; quality overrides the state, and a continuing value never drives an animation |
| [0028](0028-the-gateway-serves-over-tls.md) | The Gateway terminates TLS itself, refuses to start without either a certificate or a declaration that something in front of it is doing so, and does not enable HSTS by default |
| [0029](0029-a-trend-asks-for-the-points-it-can-draw.md) | A trend asks for the points it can draw; the server reduces in a `date_bin` query and states the width it used, and a request without `points` is still every reading |
| [0030](0030-a-tag-declares-the-range-it-expects.md) | A tag may declare the range its readings are expected in; a reading outside it keeps its value and its reported quality and is marked beside them, null means nothing is declared, and it is not an alarm |

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
