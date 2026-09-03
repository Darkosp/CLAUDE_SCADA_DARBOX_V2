# ADR-0001 — Tag identity and hierarchy

**Status:** Accepted
**Date:** 2026-09-03

## Context

SCADA_DARBOX is a horizontal SCADA platform, conceptually benchmarked against
Ignition. Every tag needs a way to be organized and browsed, and every tag's
values need to be historized and referenced (by alarms, bindings, screens)
reliably over the life of an installation — often many years.

A known weak point in tree-organized SCADA tools (Ignition included) is using
the browse path itself as the tag's identity: renaming a folder or moving a
tag for organizational reasons can break historian continuity or require a
manual migration. As a product meant to be deployed consistently across many
customer installations (not a one-off project per customer), the platform also
needs enough structural consistency that generic tooling — a site selector, a
cross-site view, bulk import — can be built without every installation
inventing its own conventions. Finally, folder position must never silently
carry functional meaning (driver behavior, alarm class, security), since an
operator reorganizing tags for readability must never be able to accidentally
change what a tag does or who can see it.

## Decision

1. Every tag has a stable, immutable internal ID (UUID), assigned at creation.
   All internal references — historian rows, alarm configuration, screen
   bindings — use this ID, never the display path.
2. **Site** is a first-class entity (own ID, name, timezone, metadata), not
   just a folder-name convention. Every device and tag belongs to exactly one
   Site. A single deployment supports multiple Sites from day one.
3. **Device** is a first-class entity (own ID, driver/connection
   configuration, optional UDT type reference) that owns a set of tags.
4. Below Site, the browsing hierarchy (Area, Line, or whatever an
   installation needs) is free-form nested folders — no fixed depth or
   naming is enforced beyond Site being the mandatory root.
5. The human-visible hierarchical path (e.g. `Site/Area/Device/Signal`) is a
   derived, mutable display label computed from these entities. Renaming or
   reorganizing it never changes tag identity or affects historized data.
6. Folders carry no functional behavior. Driver/connection configuration
   lives on Device. Alarm class and security/permission scoping, where
   needed, are explicit attributes on Site, Device, or Tag — never inferred
   from position in the browse tree.

## Consequences

Reorganizing tags for readability is always safe and never a migration.
Multi-site support exists without a future retrofit. Generic tooling (site
selectors, cross-site dashboards, bulk import) can be built against Site and
Device as real entities instead of parsing path strings. Security and alarm
configuration stay explicit and auditable rather than implicit in tree
position.

The cost: Site and Device require their own data model and APIs from the
start, rather than being encoded as string prefixes on a path. The visible
path becomes a computed/display concern (an ID ⇄ path resolution layer),
not a trivially stored value.

## Verified in review by

- Renaming or moving a tag/folder in the browse tree does not change the
  tag's ID, and historian queries by ID return unbroken history across the
  rename.
- A folder move never changes a tag's or device's alarm class or security
  scope as a side effect.
- Any test or demo dataset includes at least two Sites in a single
  deployment.
