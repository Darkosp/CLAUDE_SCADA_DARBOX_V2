# ADR-0005 — Units carry dimension, not free text

**Status:** Accepted
**Date:** 2026-09-03

## Context

Every analog tag needs a unit of measure. Storing it as a free-text label
(e.g. `"kW"`, `"bar"`, `"degC"`) is simple but purely decorative: it cannot
be used to convert between units (bar ↔ psi, °C ↔ °F), cannot catch a unit
mismatch between two tags being compared or combined, and gives no basis for
per-user or per-locale display preference (metric vs imperial). This applies
across every domain the horizontal platform will ever serve — physical
quantities and the need to convert between their units is a universal
concern, not specific to any one vertical.

## Decision

A tag's unit is a structured record, not a string: a physical **dimension**
(e.g. pressure, temperature, flow, power) plus a conversion factor to a
canonical SI representation for that dimension. Display-only unit
labels/symbols are derived from this structured record, never the other way
around.

## Consequences

Values can be converted and compared safely across units within the same
dimension, and display units can be a per-user or per-site preference
without touching stored data. The cost is a small reference table of known
dimensions/units to establish up front, and drivers/configuration must map
a device's native unit onto this structured representation rather than
passing through whatever label the device happens to report.

## Verified in review by

- A tag's unit field is never a bare string in the core data model; it
  resolves to a dimension + SI factor.
- Any new unit introduced by a driver or module maps to an existing
  dimension, or adds one deliberately rather than falling back to a raw
  label.
