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

**Status: gate met (2026-09-09).** Verified against TimescaleDB 2.17.2 and
a live Modbus simulator: live browser updates via SignalR, 145 historian
rows each with distinct source/ingestion timestamps, zero continuous
aggregates and compression disabled on the hypertable, and — the gate's
most important check — a device going offline surfaces as Bad quality on
its tags, not a fabricated zero/false. That last check caught a real defect
(see ADR-0003's rationale): the driver was fabricating `NaN`/`false` for
unreadable values, which both broke JSON serialization and defeated the
whole point of a quality field. Fixed by making tag values genuinely
nullable end to end when quality is Bad.

**Constraint:** historian writes use plain TimescaleDB hypertables only —
no continuous aggregates and no native compression. ADR-0006 flags those
specific features as TSL-licensed, pending legal review before the product
relies on them; the Phase 1 test gate does not require them, so Phase 1
does not use them. Revisit only once ADR-0006's licensing question is
resolved.

## Phase 2 — Tag browsing and device management

**Scope:** the hierarchical browse tree UI (ADR-0001), device configuration
through the UI (add/edit a Device and its connection, no code required),
dimensioned unit display (ADR-0005), and a basic historical trend chart for
a tag.

**Test gate:** a new device and its tags can be added and browsed entirely
through the UI, with a working trend chart.

**Status: gate met (2026-09-09), PR open for review (#3).** Verified live
in the browser, not just by test: on a second, empty Site (Bitola), a new
Device (Bitola Pump 1) and a new Tag (Header Pressure, unit `bar` from the
dimensioned-unit picker) were created entirely through the UI, appeared in
the tree immediately, began scanning live at Good quality with no Gateway
restart, and the trend chart collected history from the moment of
creation — derived path `Bitola/Bitola Pump 1/Header Pressure` displayed
correctly. 46 tests pass; the integration tests' database-unavailable skip
path (see below) was separately confirmed against an unreachable host
(5 Skipped, 0 Passed, reported as `Skipped!`).

Two real defects surfaced by using the feature, not by reading the code:
the trend chart was drawing a straight line through a gap where the
Gateway had been down for a rebuild, inventing readings no device ever
produced — the same class of mistake ADR-0003 exists to prevent (missing
or Bad data must never be presented as if it were a real value), just in
the chart layer instead of the driver layer this time. Fixed by breaking
the line at any gap wider than 4× the median sample interval. Separately,
a form field named `tagName` shadowed the standard
`HTMLFormElement.tagName` property (named form controls become properties
of their `<form>` element), which broke a DOM-walking tool relying on
`element.tagName`; renamed the field. The demo seed dataset was also found
with only one Site, short of ADR-0001's own review criterion ("any test or
demo dataset includes at least two Sites") — fixed in the seeder.

**Implementation notes (no new ADR needed):**

- The browse tree needs a Folder concept (a grouping node above Device/Tag
  with no data-producing behavior of its own). This is already covered by
  ADR-0001's tag hierarchy — a Folder is a display/organizational node the
  existing hierarchy already allows, not a new kind of identity requiring
  its own decision. Config CRUD for it goes through the same Dapper path
  as Tenant/Site/Device/Tag (ADR-0008). Shape: `Folder(Id, SiteId, ParentFolderId,
  Name)` — `SiteId` required (Site is the mandatory root per ADR-0001 §2/§4),
  `ParentFolderId` nullable and self-referential (null = directly under
  Site), no depth limit, matching ADR-0001 §4's "free-form nested folders".
  `Folder` carries `UNIQUE(SiteId, Id)`, and its self-referential FK is the
  composite `(SiteId, ParentFolderId) REFERENCES Folder(SiteId, Id)` — a
  folder must not be nestable under a parent folder from a different Site,
  same reasoning as the Device invariant below, one level up.
- `Device.FolderId` is nullable and purely organizational (ADR-0001 §6:
  folders carry no functional behavior); `Device.SiteId` stays required and
  is the actual tenant/security scope (ADR-0004). A Device with no
  `FolderId` sits directly under its Site, matching Phase 1's flat layout
  unchanged. The invariant that a Device's Folder must belong to the same
  Site as the Device is enforced as a **database-level composite foreign
  key** — `(SiteId, FolderId) REFERENCES Folder(SiteId, Id)` — not an
  application-level check. This is a security boundary (cross-tenant/
  cross-site placement), and a check living only in application or
  repository code is exactly the kind of thing a future code path (bulk
  import, a new endpoint, a repository bug) can silently bypass; the same
  composite-FK pattern applies to Folder's own self-referential FK above.
- Browse tree API is one recursive `GET /api/sites/{siteId}/tree` returning
  the whole Folder/Device/Tag tree for that Site in a single response, not
  lazy per-node children. Phase 2's test gate is deliberately small (one
  new device and its tags), and CLAUDE.md's "build only what the phase
  scopes" applies here too — lazy pagination solves a scale problem this
  phase doesn't have yet. Revisit (add a lazy `children` endpoint alongside,
  not instead of, this one) only if a real deployment's tree size makes the
  eager fetch a demonstrated problem — no new ADR needed for that either,
  since it wouldn't remove or break the existing endpoint.
- Adding a Device through the UI must make its tags live without a Gateway
  restart (TagCatalog reload / hot-add on new-device-saved). This is a
  service lifecycle detail within Phase 2's own scope ("no code required"
  to add a device), not an architectural decision — it doesn't get a
  separate ADR.
- The trend chart is a small hand-written inline SVG line chart, not a
  charting library. ADR-0006 doesn't name one, and adding one would be
  exactly the kind of dependency-outside-the-table CLAUDE.md requires an
  ADR for. Revisit only via a new ADR if a real need (zoom/pan, multiple
  overlaid series) makes a library clearly worth the dependency — the
  Phase 2 gate doesn't need one.

**Before Phase 3 starts:** deletion semantics for Folder/Device/Tag config
were deliberately left out of Phase 2 — what happens to a deleted Device's
historian rows (which ADR-0001 says must outlive its config) and to a
Folder's contents when the Folder is deleted is a decision, not an
implementation detail, and it wasn't required by Phase 2's test gate. It
needs to be closed, in the design conversation, before Phase 3 scope
begins.

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
