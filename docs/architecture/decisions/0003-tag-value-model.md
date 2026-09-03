# ADR-0003 — Tag value model

**Status:** Accepted
**Date:** 2026-09-03

## Context

As a horizontal platform, tags must represent more than analog measurements:
digital/discrete states, text values (recipe names, batch/series IDs,
operator IDs), and coded states with human-readable labels (stop-reason
codes, machine states) all appear in real installations. A value modeled as
a single `double` cannot represent these without lossy workarounds (e.g.
encoding text as a number).

Separately, every value needs to carry more than the number itself: the
timestamp when the source device actually captured it (not when it arrived
at the server), and a quality indicator, so the system can tell "the sensor
reported zero" apart from "the sensor is offline or faulted" and can
correctly place a late-arriving value (from a device that was briefly
disconnected) at its true point in history rather than at arrival time.

## Decision

A tag value is a strict discriminated union with a fixed set of kinds:

- **Numeric** (`double`) — analog measurements
- **Boolean** — simple digital states
- **Text** (`string`) — free-text values (batch ID, recipe name, operator)
- **Discrete** (integer code + optional label lookup) — enumerated states
  such as stop-reason codes or machine states, human-readable via a
  lookup table, not by re-encoding text as a number

Every value, regardless of kind, carries:

- **Source timestamp** — when the originating device captured it (UTC)
- **Quality** — Good / Uncertain / Bad / Stale, modeled on OPC UA's
  quality-code concept rather than invented from scratch
- A separate **write/ingestion timestamp**, distinct from the source
  timestamp, so out-of-order or delayed writes (e.g. from a device that was
  briefly offline) are stored at their true source time, not at arrival time

The value model is a closed set of kinds for now, not an open/free-form
(e.g. JSON blob) type — both for type safety and because a typed,
column-oriented shape performs and indexes better in a time-series store
than a generic blob. A new kind (e.g. a structured/array value) is added
only when a concrete need for one actually appears, per ADR-0002's
core/module discipline.

## Consequences

The historian schema must support four typed value columns (or an
equivalent typed structure) instead of one, and every driver must map its
native quality/status codes onto the shared Good/Uncertain/Bad/Stale model.
In exchange, the platform can represent the full range of real tag data
without lossy encoding, correctly handles delayed/out-of-order data from
devices that reconnect after an outage, and never confuses "a real zero"
with "no valid reading."

## Verified in review by

- Any new tag value kind proposed in a PR is justified by a concrete,
  current need — not spec'd speculatively.
- Every driver implementation maps its native status/quality codes onto
  Good/Uncertain/Bad/Stale rather than always reporting Good.
- Historian writes carry both a source timestamp and an ingestion
  timestamp, and out-of-order source timestamps are accepted and stored
  correctly, not rejected or silently reordered to arrival time.
