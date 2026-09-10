# ADR-0010 — UDT semantics: live-reference type with materialized instance tags

**Status:** Accepted
**Date:** 2026-09-10

## Context

Phase 4 scopes device templates (UDTs) — "a device type defined once,
instantiated many times" — with a test gate of three devices instantiated
from one UDT through configuration alone. That phrasing leaves open the
question that actually decides the data model: when a Device is
instantiated from a UDT, is it a one-time copy of the template, or a live
reference to it?

A one-time copy is simple — instantiate, then the instance is independent
— but it quietly defeats what a UDT is for. If fifty pumps are
instantiated from a "Pump" template and the template's Modbus register
for discharge pressure turns out to be wrong, "define once" should mean
fixing it once; a copy-based model means fixing it fifty times, which is
exactly the problem UDTs exist to remove.

A live reference is the real answer, but two existing decisions constrain
how it can work. ADR-0001 requires every tag to carry its own stable,
immutable ID for historian continuity — so a UDT's tags cannot be shared
or virtual rows; every instance needs its own real, materialized Tag with
its own ID, even though its shape comes from the template. And a UDT
describing three pumps needs those three instances to actually differ —
each at its own protocol address — or "three devices" from one template
would be three identical devices pointed at the same register, which
does not satisfy the gate at all.

## Decision

A UDT is a first-class type, `device_template`, with a name and a set of
`device_template_tag` rows: name, dimension/unit (ADR-0005), value kind
(ADR-0003), and an address template — a string carrying named
placeholders (e.g. `{unitId}:40001+{offset}`).

A Device instantiated from a template (`device.template_id`, nullable, FK
to `device_template`) carries a small set of named parameters
(`device_template_parameter`, e.g. `host`, `unitId`, `offset`) supplied at
instantiation. Placeholder syntax and address resolution are entirely a
driver module's concern — Core and the UDT engine treat both the address
template and the instance parameters as opaque named strings, never
protocol-specific values, so this stays inside ADR-0002's core/module
boundary: Core has no idea what `{offset}` means for Modbus versus OPC UA.

Instantiating a Device from a template immediately materializes one real
Tag row per template tag, each with its own stable ID (ADR-0001) and its
address resolved from that Device's parameters. A materialized tag is an
ordinary tag in every other respect — scanned, historized, alarmable —
with only an optional, informational link back to the
`device_template_tag` it came from.

Editing a template — adding, renaming, or removing a
`device_template_tag` — propagates to every instance of that template
automatically and immediately, not as a staged or reviewed operation.
Adding a tag materializes it (address resolved from each instance's
existing parameters) on every instance; removing one soft-deletes the
corresponding materialized tag on every instance, through the same
`tag_active` mechanism as any other tag deletion (ADR-0009) — history is
unaffected either way. This was a deliberate choice, made explicitly
rather than defaulted into: a template's entire purpose is that fixing it
fixes every instance, and a staged/reviewed alternative would need a
review workflow and UI of its own that Phase 4's gate does not ask for.

Per-instance override of a materialized tag is out of scope for Phase 4
— an instantiated tag follows its template with no divergence mechanism.
Nothing here forecloses adding overrides later if a real need arises.

## Consequences

A template edit fixes every instance in one action, which is the entire
point of building UDTs — but it also means a template edit's blast radius
is every device ever instantiated from it, with no confirmation step and
no built-in undo beyond editing the template back. Materializing a real
Tag row per instance (rather than a shared or virtual tag) costs storage
and write amplification on template edits, in exchange for the historian
continuity ADR-0001 requires and a shared tag could never provide.

## Verified in review by

- Instantiating a Device from a template creates one Tag per template
  tag, each with its own ID and its own address resolved from that
  Device's parameters — never a shared or virtual tag.
- Two Devices instantiated from the same template with different
  parameters produce tags with different real addresses.
- Adding a tag to a template materializes it, with a correctly resolved
  address, on every existing instance of that template — no code change,
  no manual per-device step.
- Removing a tag from a template soft-deletes the corresponding tag
  (ADR-0009) on every instance; its prior historian rows remain queryable
  by ID and still display a name.
- A test instantiates at least three Devices from one template (matching
  this phase's own test gate) and confirms all three scan correctly at
  distinct addresses.
