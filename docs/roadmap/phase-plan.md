# Phase Plan

Each phase has an explicit scope and a test gate. A phase is not "done" by
calendar time or by the perceived amount of code written — it's done when
its gate is met. No phase begins before the previous phase's gate is met.

## Phase 0 — Architecture and foundations

**Scope:** the [Phase 0 Architecture](architecture/phase-0-architecture.md)
document, the foundational [ADRs](architecture/decisions/README.md), this
phase plan, and a CLAUDE.md hand-off for implementation. No application code.

**Test gate:** every foundational decision (ADR-0001 through ADR-0006) is
closed and recorded; the Phase 0 architecture document and this plan exist
in the repository.

## Phase 1 — Core skeleton

**Scope:** the smallest possible end-to-end vertical slice that proves the
architecture, not a feature-complete system. A real Tag engine on
PostgreSQL/TimescaleDB (Tenant → Site → Device → Tag, ADR-0001/0003/0004),
one driver module — Modbus TCP, as the simplest path to a working
end-to-end proof against a simulator — the Web API and SignalR push, and a
minimal Angular page that displays one live tag value updating in the
browser. No alarms, no auth, no UDTs, no tag browse tree yet.

**Test gate:** a value from a simulated Modbus device flows
Driver → Tag engine → (historian row + browser update) end-to-end, verified
against a live simulator.

## Phase 2 — Tag browsing and device management

**Scope:** the hierarchical browse tree UI (ADR-0001), device configuration
through the UI (add/edit a Device and its connection, no code required),
dimensioned unit display (ADR-0005), and a basic historical trend chart for
a tag.

**Test gate:** a new device and its tags can be added and browsed entirely
through the UI, with a working trend chart.

## Phase 3 — Alarms

**Scope:** the alarm engine — thresholds, states (active / ack / shelved),
and an alarm banner/summary screen in the web client.

**Test gate:** a simulated out-of-range value produces a visible alarm that
can be acknowledged.

## Phase 4 — Additional drivers and UDTs

**Scope:** at least one more driver module (OPC UA and/or MQTT), and device
templates (UDTs) — a device type defined once, instantiated many times.

**Test gate:** three devices are instantiated from one UDT through
configuration alone, no code changes.

## Phase 5 — Users, roles and security

**Scope:** token-based authentication, role-based permissions (needs its
own ADR before this phase starts), and an audit trail. Not started until a
permissions-model ADR exists.

**Test gate:** a non-privileged user cannot write to a tag or view a site
outside their permitted scope.

## Phase 6 — Deployment packaging for both topologies

**Scope:** Docker Compose packaging for the on-premises topology, and the
edge-agent-plus-cloud-Gateway split with MQTT store-and-forward for the
cloud topology (Phase 0 Architecture, "Deployment topologies").

**Test gate:** the full stack runs in Docker Compose, locally, in both
configurations.

## Later (not yet scoped)

HMI screen editor and a real component library, the scripting engine
(Jint), reporting. These are deliberately left unscoped until a phase above
creates a concrete need for them, per ADR-0002's module discipline.
