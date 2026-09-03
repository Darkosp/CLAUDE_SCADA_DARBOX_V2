# Phase 0 Architecture

This document ties the individual [architecture decisions](decisions/README.md)
into one coherent picture of the system. It describes shape, not code — Phase 0
produces no application source (see the repository [README](../../README.md)).

## Vision

SCADA_DARBOX is a horizontal, modular, web-based SCADA platform — a product,
not a per-customer project. It is conceptually benchmarked against Ignition
(Inductive Automation): a browser-native client, a single deployable core
with optional modules, and no per-tag/per-client licensing.

## Components

**Gateway** — the central .NET process. Hosts the Web API, the SignalR
real-time push hub, the tag engine, the driver framework, the alarm engine,
and users/roles/auth/audit. This is the "core" defined in ADR-0002.

**Tag engine** — the Tenant → Site → Device → Tag model (ADR-0001, ADR-0004),
holding live values in the typed union shape defined in ADR-0003, with units
resolved through the dimensioned model in ADR-0005.

**Driver framework + drivers** — the framework is core; concrete drivers
(OPC UA, Modbus TCP/RTU, MQTT/Sparkplug B — ADR-0006) are modules composed
at compile time (ADR-0002) against the framework's protocol-agnostic
contract. A driver reads values into the tag engine and, where a tag is
writable, carries writes back out to the device.

**Historian** — sits behind the storage-abstraction interface defined by
core; PostgreSQL + TimescaleDB is the concrete implementation (ADR-0006).
Accepts the typed, quality-and-timestamp-carrying values from ADR-0003,
including out-of-order/delayed writes.

**Edge agent** — a separate, small .NET Native AOT process (ADR-0006) that
runs on the customer's site. It hosts the driver connections that must sit
physically close to the field devices, and forwards data to the Gateway.
Present in both deployment topologies below, but only load-bearing for the
cloud topology's network boundary (see below).

**Web client** — the Angular application (ADR-0006). A thin, browser-based
client; nothing is installed on operator machines. Receives live tag values
over SignalR and reads/writes through the Web API.

## Deployment topologies

Both topologies run the same codebase (ADR-0002's core/module composition
does not change); they differ only in where each component physically runs.
Every deployment is single-tenant (ADR-0004) — one instance per customer,
never shared.

**On-premises:** Gateway, historian, driver connections and the Angular web
client all run on the customer's own server(s), inside their LAN. The edge
agent is not architecturally necessary here — drivers can talk to devices
directly, since everything is already local — though the same agent process
may still be used for consistency. Remote users reach the system over the
customer's VPN; local users reach it directly over the LAN.

**Cloud:** Gateway, historian and the Angular client are hosted centrally.
The edge agent runs on the customer's site, holds the driver connections to
the actual field devices, and forwards data to the Gateway over an
**outbound** MQTT/Sparkplug B connection secured with TLS. This deliberately
avoids requiring any inbound firewall port at the customer's site — a
recurring blocker with industrial/OT security teams. The edge agent buffers
locally (store-and-forward) when the link to the Gateway is down, and
reconciles on reconnect, so field-level operation never depends on the
cloud link being up.

## Data flow (read path)

Device → Driver (in the edge agent, or in-process on-premises) → Tag engine
(current value updated) → fan-out to: (a) Historian write, (b) SignalR push
to every subscribed web client. A write from an operator follows the
reverse path: Web client → Web API → Tag engine → Driver → Device, for any
tag marked writable.

## Cross-cutting concerns carried by every component

- **UTC internally, local time zone only at display** — consistent with the
  source-timestamp model in ADR-0003; not yet its own ADR, called out here
  as a working assumption to formalize if it needs to become binding.
- **TLS in transit everywhere** — customer LAN traffic included, not just
  the cloud topology's edge-to-Gateway link.
- **Quality-aware everywhere** — any component that reads a tag value must
  be able to represent "no good value" (ADR-0003's quality field), not
  silently substitute zero or the last-known value without marking it stale.

## Explicitly open (future ADRs, not yet decided)

- Authentication mechanism and provider(s) (e.g. token-based, whether an
  external identity provider or directory integration is supported)
- HMI screen/editor implementation details beyond "component-based, not
  static pictures" (implied by the base HMI framework in ADR-0002)
- Alarm notification channels and escalation policy specifics
- Any regulatory/compliance module — none is assumed; added only if and
  when a real deployment needs one (ADR-0002's module discipline)
