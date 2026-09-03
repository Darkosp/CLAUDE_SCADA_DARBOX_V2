# ADR-0002 — Core/module boundary and composition

**Status:** Accepted
**Date:** 2026-09-03

## Context

SCADA_DARBOX is a horizontal, domain-agnostic platform meant to serve many
industries and customers from one codebase, not a one-off project rebuilt per
customer ("platform shape, application scope"). This only works if there is a
strict, checkable line between what is universal (core) and what is
domain/protocol-specific (modules) — otherwise domain assumptions leak into
the core over time and every new customer requires touching code that should
have been stable.

A separate question is how modules are composed: dynamically loaded runtime
plugins give extensibility without a rebuild, but bring real risks —
version-compatibility drift between core and plugin, harder-to-reason-about
failure modes, weaker compile-time guarantees — that matter more for a
long-lived industrial system than the convenience of hot-swapping.

## Decision

**Core** (always present, domain-neutral):
- Tag engine — the Site / Device / Tag model and hierarchy (ADR-0001)
- Driver framework — a protocol-agnostic contract/SDK for plugging in
  drivers; the core has no knowledge of any specific protocol
- Historian abstraction — an interface for storing/reading historized
  values, independent of the concrete backend
- Alarm engine — state machine (active / ack / shelved / escalation)
  without domain-specific thresholds or logic
- Users, roles, authentication, audit trail
- Web API and real-time push (SignalR)
- Base HMI framework — screen container, component model, tag bindings —
  without any domain-specific widget library

**Modules** (optional, added only when a real, concrete need exists):
- Concrete driver implementations (Modbus TCP, OPC UA, MQTT, etc.), each
  plugged into the driver framework
- Domain-specific HMI components/widgets
- Domain-specific derived tags or calculations
- Industry-specific report/dashboard templates
- Compliance packages (e.g. a regulated-industry audit layer), added only
  for a deployment that actually requires them

**Composition:** modules are composed at compile time, as separate projects
registering against core's public contracts — not dynamically loaded at
runtime. A new module requires a rebuild and redeploy; in exchange, the
system avoids runtime plugin version drift and keeps strong compile-time
guarantees, which matters more here than hot-swappable extensibility.

## Consequences

Core stays domain-neutral and stable across every deployment; adding a new
protocol driver or a domain-specific screen can never accidentally require
touching core code. The line between core and module must be judged
carefully as each real module appears — nothing is speculatively built into
core "just in case." The cost is that a new module always needs a build and
deploy cycle; there is no plugin marketplace or hot-loading in this version
of the platform.

## Verified in review by

- No core project references a specific protocol library, a domain
  vocabulary, or an industry regulation.
- Every module lives in its own project and depends only on core's public
  contracts, never the reverse.
- No reflection-based dynamic assembly loading is introduced for
  extensibility.
