# ADR-0024 — A screen is configuration, and a component shows its own quality

**Status:** Accepted
**Date:** 2026-10-03
**Complements:** ADR-0002 (a closed, compile-time component set — no plugin loading), ADR-0003 (a
value carries quality and a source time, and a value with no honest reading is not invented), ADR-0004
(a Site-scoped entity is transitively Tenant-scoped), ADR-0008 (Dapper for config tables, DbUp owning
schema), ADR-0009 (soft delete via an active-row view), ADR-0011 (Site-scoped access, and a Site the
caller cannot see answers 404), ADR-0015 (names unique within their parent).

## Context

`phase-0-architecture.md` has said since the beginning that the HMI is "component-based, not static
pictures", and has listed the implementation details as explicitly open. Phase 8 is where that stops
being open, because the features below an HMI are all finished: values, quality and source times
already reach the client live and per Site over SignalR (`tag-stream.ts`), alarms are journalled and
replayed, and permissions are enforced everywhere. A screen is now the only thing between an
operator and everything that already works.

Three constraints come from documents that already bind, and they matter more than any preference:

- **ADR-0002 refuses reflection-based plugin loading.** Whatever a component is, the set of them is
  fixed at compile time and a deployment cannot add one.
- **ADR-0003 refuses an invented value.** A screen is where values are *looked at*, so it is where a
  fabricated one would do the most damage — a stale number rendered as a live one is precisely the
  mistake the whole project is organised against.
- **ADR-0011's Site rule.** A screen belongs to a Site, and an id on a Site the caller cannot see is
  indistinguishable from one that does not exist.

Two questions had to be answered and only one of them is about code. **What is a screen** — a row and
some rows under it, or a document, or a file? — and **what happens when a screen asks for something
the person reading it is not allowed to see**, which has no obvious right answer and a wrong one that
looks fine.

## Decision

**1. A screen is a row, and so is everything on it.**

Two tables, both soft-deleted and read through an `_active` view like every other config table
(ADR-0009). A screen is `(id, tenant_id, site_id, name, position)`. A component is
`(id, screen_id, position, row_index, column_span, kind, title, tag_id)`. There is no document, no
file and no code: what a screen is, is what these rows say, and the API is the only way one is
created. The client is a renderer, not a compiler — a screen it cannot read is one it cannot show,
and it says so.

**2. Layout is a row of columns, not a canvas.**

A component occupies a `row_index` and a `column_span` of a twelve-column grid. Two components in
the same row sit side by side in the order their `position` gives them. That is the whole layout
model.

It is deliberately the least a real screen needs. Absolute positioning is not modelled, because a
canvas is a promise about pixels and resolutions that this project would then have to keep, and
because a screen that only looks right at one window size is a screen that is wrong at most of them.
Nesting is not modelled either, for the same reason — the schema keeps room for both without
deciding them, and the next slice can add either by a new ADR rather than by stretching this one.

**3. The component set is closed, small, and fixed at compile time.**

Five kinds, and a deployment cannot add a sixth (ADR-0002):

| Kind | Shows | Needs a tag |
|---|---|---|
| `label` | Text an author wrote — a heading, a unit, an instruction. | no |
| `value` | One tag's value, its quality and its source time. | yes |
| `trend` | One numeric tag's recent history, on the existing trend chart. | yes |
| `alarms` | The standing alarms of this screen's Site. | no |
| `status` | One tag's quality as a state an operator reads at a glance. | yes |

An unknown `kind` is **refused when the screen is saved**, with the name of the kind that is not
understood. It is not stored and skipped at render time: a screen saved with a component nobody can
draw is a screen that is silently missing something, which is the failure mode this whole decision
exists to avoid.

**4. A binding is a tag id, and every component that reads one has somewhere for quality to show.**

`value`, `trend` and `status` carry a `tag_id`; `label` and `alarms` must not. A component of a kind
that needs a tag without one is refused at save time, and so is a `label` that carries one.

**There is no component that prints a value with nowhere for its quality to go.** The value
component shows quality and source time beside the number; `status` is quality and nothing else. This
is ADR-0003 applied where it is most visible, and it is why the set is five kinds rather than a
generic "display" — a generic display would let a screen be built that cannot say "this is not a
current reading".

**5. A binding to something the reader may not see renders as unreadable, not as absent.**

If a component names a tag the caller cannot see — another Site, or a tag deleted since — the
component **is still rendered**, saying that it cannot be read. It is not hidden and it does not
render as zero or as an empty box.

The reasoning is the reason ADR-0011 answers 404 rather than 403, seen from the other side. Hiding
the component would make a screen look *complete* while showing less than it was built to show, and
an operator has no way to know a tile is missing from a screen they did not author. "This reading is
not available to you" is a true sentence; a blank space is not a sentence at all.

**6. A screen is Site-scoped exactly as every other entity is.**

Reading needs `CanView` on the screen's Site, editing needs `CanOperate`, and a screen whose Site the
caller cannot see answers **404, not 403** (ADR-0011). A screen's name is unique within its Site
among live rows, ignoring case, and a duplicate answers 409 naming what exists (ADR-0015).

**7. A new Site is born with one screen.**

Creating a Site seeds a screen holding the components that make it useful with no authoring: a
`label` naming the Site, an `alarms` component, and nothing bound to a tag — because a Site with no
devices has no tags, and a bound component would be unreadable from its first moment.

This is a lesson Phase 6.5's walk recorded rather than a preference: the first screen a new user saw
was empty, and an empty screen is indistinguishable from a broken one.

**8. Screens are read-only in this phase.**

A writable tag is marked writable on a screen; acting on it is not built here. Writing from a screen
puts the Operator check, the audit entry and the write path's own refusals behind a button rather
than a form, and that is a decision about a screen rather than about storage — so it is the next
slice's, and this ADR does not half-take it.

**Closed on 2026-10-05 by [ADR-0026](0026-operating-from-a-screen.md)**, which is the next slice this
decision named. That ADR keeps the marking and builds the action: only a `value` component offers the
control, the server still decides whether it is offered, the write is confirmed in a dialog showing the
current reading, and it is never held or reported done before the device has answered. **The decision
above is left standing rather than rewritten**, because it is what was true of this phase and it is the
reason the next ADR exists.

## Consequences

- **A screen is data, so it can be edited by anything that can call the API** — including a builder
  that does not exist yet, which will be a client over these rows rather than a new model.
- **The renderer cannot be surprised.** Every component it is given is one of five it knows, because
  a sixth could not have been saved. That is what ADR-0002's no-reflection rule buys here.
- **A screen can be wrong about a tag and cannot be wrong about quality.** A binding may name a tag
  that has gone; it will say so. It can never show a number without the reader being able to see
  whether that number is current, which is the only guarantee this ADR makes about correctness.
- **Hiding is not available as an authoring choice.** There is no "hide when not permitted" flag, on
  purpose (decision 5). A deployment that wants a tile gone must delete it, and that changes what
  every reader sees — which is the honest version of the same wish.
- **`position` is an integer, and reordering rewrites the row.** For a screen of a few dozen
  components that is a single update and not a problem; it is recorded because it is the kind of
  thing that becomes one at a scale this project has not reached.
- **The five kinds will not be enough.** The first real screen will want something else, and the
  answer is a new ADR adding a kind — not a generic component, and not a scripting escape hatch.
  `phase-plan.md`'s "Later" holds Jint for the reason ADR-0002 gives, and an HMI is exactly where a
  project gives in and adds one.
- **Not decided here.** Whether a screen can be shared across Sites, or template a family of Sites;
  whether components nest; whether a screen can be exported and imported between deployments. All
  three want a deployment that needs them.

## Verified in review by

- A screen saved with an unknown component kind is **refused with the kind named**, and nothing is
  stored — proved by asking for the screen afterwards and finding it unchanged.
- A component whose kind needs a tag, saved without one, is refused; and a `label` carrying a tag is
  refused. Both by name.
- A component bound to a tag the caller cannot see renders as unreadable — asserted **through the
  same path the renderer uses**, not by inspecting the row, so the decision is tested where it is
  applied.
- A screen on a Site the caller cannot see answers 404, and its name is not enumerable by trying it.
- A duplicate screen name on one Site answers 409 naming what exists, and the same name on another
  Site is accepted.
- Creating a Site seeds a screen, and a viewer on that Site sees it with a seeded component in it.
- The layout survives a round trip unchanged, including `row_index`, `column_span` and `position`.
- A component bound to a deleted tag reads as missing rather than as a value, and the screen still
  renders its other components.
