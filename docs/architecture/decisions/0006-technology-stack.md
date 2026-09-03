# ADR-0006 — Technology stack

**Status:** Accepted
**Date:** 2026-09-03

## Context

The core/module boundary (ADR-0002) and the tag/value/tenant model
(ADR-0001, 0003, 0004, 0005) are settled independently of any specific
technology. This ADR fixes the concrete stack the implementation will use,
so Claude Code has an unambiguous target rather than re-deciding it per
phase.

## Decision

| Layer | Choice | Why |
|---|---|---|
| Backend | .NET, latest LTS release | long-term support; Native AOT for the edge agent; the most mature OPC UA stack available in any managed language |
| Frontend | Angular, latest stable release, signals/zoneless reactivity model | signals are a natural fit for a live tag value; strong typing suits a long-lived enterprise HMI |
| Historian / database | PostgreSQL + TimescaleDB | open, standard SQL; configuration data and time-series data live in one engine. **Flag:** TimescaleDB's continuous aggregates and native compression are licensed under the Timescale License (TSL), not Apache 2.0 — get legal confirmation before relying on them if the product is ever resold or sublicensed |
| Real-time push | SignalR, with a Redis backplane added only once multiple backend instances are actually running | native to .NET; no third-party real-time layer needed at single-instance scale |
| Driver layer | OPC UA .NET stack (OPC Foundation), NModbus (Modbus TCP/RTU), MQTTnet (MQTT / Sparkplug B) | mature, widely used libraries; each protocol is its own module behind the driver framework (ADR-0002) |
| Scripting / extensibility | JavaScript via Jint (or ClearScript/V8) plus a typed expression language | a modern, sandboxed scripting option, instead of Ignition's legacy Jython 2.7 |
| Edge agent | .NET, Native AOT publish | small, fast-starting, low-memory native binary; keeps the whole stack to one language instead of adding Go/Rust just for the edge |
| Deployment | Docker Compose; Kubernetes only if and when actually needed | matches the single-tenant-per-instance deployment model already agreed |

## Consequences

Every phase of implementation targets a fixed, unambiguous stack — no
per-phase re-litigation of language or library choices. The flagged
TimescaleDB licensing point must be resolved (legal check) before the
historian module leans on continuous aggregates or compression in a
resold/sublicensed product; until resolved, those specific TimescaleDB
features should be treated as at-risk rather than load-bearing.

## Verified in review by

- No dependency is introduced outside this table without a new or amended
  ADR.
- Any use of TimescaleDB continuous aggregates or compression is flagged
  for legal review before merge, until ADR-0006 is amended to record that
  the check happened.
