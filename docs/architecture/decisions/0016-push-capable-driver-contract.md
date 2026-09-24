# ADR-0016 — A driver may push, and a driver says which it does

**Status:** Accepted
**Complements:** ADR-0002 (Core is domain-neutral; modules are compile-time
composed) and ADR-0003 (every value carries type, source timestamp and
quality). Neither is superseded.
**Date:** 2026-09-24

## Context

Every driver the system has is polled: the scan service asks a device for a
value on an interval, and the value arrives as the answer to a question.
Modbus works that way because Modbus is that way. OPC UA was made to fit it in
Phase 4 — the driver subscribes underneath, but the contract above it is still
"give me your current value when I ask".

MQTT cannot be made to fit it. A device that publishes when its value changes
has no answer to "what is it now": there is nothing to read, only something
that arrived, or did not. Phase 4 saw this and deferred the MQTT driver rather
than deform the contract, which was the right call and is why the driver
framework is still clean.

Phase 7 makes it unavoidable. An edge agent is, from the cloud Gateway's point
of view, a device that pushes: samples appear when the link allows, in batches,
carrying timestamps from minutes ago.

The tempting shortcut is to let a pushing driver keep a "last value" that the
scan service polls. It would work, and it would quietly destroy the property
ADR-0003 exists to protect. A poll of a cached value produces a *reading* at
poll time; if nothing has arrived for an hour, the cache answers anyway. The
system would then be inventing the one thing it has refused to invent since
Phase 1: a value nobody measured. The same rule that stopped a Bad Modbus read
becoming a zero, and a trend line being drawn across a gap.

## Decision

**A driver declares whether it is polled or pushing, and Core treats the two
differently by contract, not by convention.**

- A **polled** driver answers a read for a tag, as today. Nothing about the
  existing drivers changes.
- A **pushing** driver is *started*, and thereafter hands Core samples as they
  arrive: one call carries one or many, each with its own tag id, value,
  source timestamp and quality (ADR-0003, unchanged). Core never asks a
  pushing driver what a value is now, because it has no honest answer.
- **The scan interval does not apply to a pushing driver**, and configuration
  must not offer one. A field that exists but does nothing is a lie the UI
  tells the person configuring it.
- **Silence is not Good.** A pushing driver declares a staleness limit; when
  nothing has arrived for a tag within it, that tag's quality becomes Bad with
  the timestamp of the last real sample, and it does not become Good again
  until something actually arrives. Silence is the only signal a pushing
  transport gives, and it must read as loss, not as "unchanged".
- Batches are ingested **in source-timestamp order per tag**, and a sample
  older than one already stored for that tag is stored, not dropped: late is
  not wrong. What must never happen is that a late arrival moves a tag's
  *current* value backwards in time.

Core gains the second shape and nothing else: no protocol, no broker, no
vocabulary (ADR-0002). `Drivers.Mqtt` and the edge link are modules, as Modbus
and OPC UA are.

## Consequences

- MQTT becomes writable as a module without touching the polled path, and the
  edge agent is a pushing source like any other.
- The tag engine now has two entry shapes to keep honest instead of one. The
  staleness rule is the part most likely to be got wrong quietly, so it is
  where the tests go.
- A pushing tag's history has real gaps in it, and the chart already draws
  those as gaps rather than lines (Phase 2). Nothing new is needed there.
- Alarm evaluation is unchanged: it runs on values as they are ingested, and a
  tag that has gone Bad through silence stops being compared, exactly as a Bad
  Modbus reading does (ADR-0003).

## Verified in review by

- The driver contract makes "polled" and "pushing" distinct, and a pushing
  driver has no read-a-value method to call.
- Configuration for a pushing device offers no scan interval, and a test fails
  if one is accepted.
- There is a test in which a pushing tag receives nothing for longer than its
  staleness limit: the tag reads Bad, keeps the timestamp of its last real
  sample, and no value is fabricated. Removing the staleness rule makes it
  fail by leaving the tag Good.
- There is a test that a batch arriving late — timestamps older than the tag's
  current value — is stored in history without changing the current value.
- Core references no MQTT type, no broker and no edge vocabulary.
