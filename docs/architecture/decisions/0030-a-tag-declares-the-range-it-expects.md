# ADR-0030 — A tag may declare the range its readings are expected in, and a reading outside it says so

**Status:** Accepted
**Date:** 2026-10-07

## Context

**The defect is a number that cannot be true, drawn exactly like one that can.** Walking the screens on
2026-10-06, `Tank 3 Level` read **`464.00 %`** — impossible for a level — and it was rendered the same way
as `4.79 bar`. That particular reading was the session's own demo data (a raw register with no scaling),
and the demo was fixed; **the question it raised was not**, and it has been open in `open-work.md` §2.0l
since: nothing in this product can say that a value is outside what it is supposed to be.

**Why the units work does not answer it.** ADR-0005 gives a tag a dimension and an SI factor, which is a
statement about **what a number means and how to convert it** — not about **whether a particular value is
possible**. `%` is a dimension; `464` of them is a fact about a register, a scale factor or a plant, and no
unit can rule on it.

**And the standard answer already exists, which is what settles the question this ADR was waiting on.** The
shape is the same in every protocol this project speaks or sits beside:

- **OPC UA Part 8 makes a declared range mandatory on an analogue item.** `AnalogItemType` "requires the
  `EURange` Property" — a `Range` of low and high engineering units ([OPC 10000-8,
  5.3.2.3](https://reference.opcfoundation.org/specs/OPC-10000-8/5.3.2.3)).
- **And it says where the out-of-range fact goes: beside the value, not in place of it.** The low 16 bits
  of a status code "are bit flags that contain additional information, but **do not affect the meaning of
  the StatusCode**… Of particular interest for DataItems is the LimitBits field", and "servers that do not
  support Limit have to **set this field to 0**" ([OPC 10000-8,
  7.3.3](https://reference.opcfoundation.org/specs/OPC-10000-8/7.3.3)).
- **IEC 61850 carries it as a quality detail bit.** Its 13-bit packed `Quality` has `validity` (good /
  invalid / reserved / questionable) and, among the detail bits, **`overflow` (bit 2)** and **`outOfRange`
  (bit 3)** — alongside `oldData`, `inconsistent` and the rest ([the bit layout, transcribed from
  IEC 61850-7-3](https://docs.rs/iec61850-rs/latest/iec61850_rs/common/struct.Quality.html)). DNP3 calls
  the same idea `OVER_RANGE` and IEC 60870-5-101/104 has an `OV` bit in its quality descriptor — names
  checked in secondary sources rather than in the standards themselves, and recorded here as such.

So the answer is not a matter of taste, and it is not a new invention: **a declared range on the tag, and a
violation reported beside the reading rather than instead of it.** What follows is only about how that fits
this code.

## Decision

**1. The range belongs to the tag's definition: `range_low` and `range_high`, nullable, in the unit the tag
already declares.**

Nullable, both-or-neither, and `low < high` — refused rather than repaired. Not on the device, because two
tags on one device can have different plausible spans; not global, because a pressure and a level do not
share one.

**Not in the alarm engine, and this is the distinction worth keeping.** OPC UA separates an item's
`EURange` from a limit alarm's `HighLimit`, and so does this project: ADR-0025 owns limits, and a limit is a
decision somebody made to watch a value, with an on-delay and a deadband and a journal row and an
acknowledgement. "Outside its range" is a statement about **plausibility**, not about a limit anyone chose
to watch, and making it an alarm would put rows in the journal for a plant behaving normally at a
configured range's edge.

**2. A reading outside the range keeps its value and keeps the quality its driver reported.** The
out-of-range fact is reported **beside** them — in range, above it, or below it — and never replaces,
clamps, rounds, masks or re-times the reading. This is OPC UA's own rule for the status code's low bits,
and it is the same rule ADR-0003 already enforces for a value nobody measured: the product shows what
arrived and says what it knows about it, and it does not put a different number where a real one was.

**3. Nothing declared says nothing.** A tag with no range gets **no status at all** — not "in range". That
is the LimitBits rule that a server without limit support sets the field to zero, and it is the same
distinction ADR-0025 drew for a null on-delay: **null is not zero**, and a deployment that has not declared
a range must not be shown a verdict nobody made.

**4. It is decided on the server, beside `readable` and `writable`** (ADR-0024 §6). One place knows the
tag's range and the reading, so one place answers, and every renderer gives the same answer. A screen that
re-derived it would be a second home for the rule, which is the defect this project has found in four
separate walks.

**5. The screen can show it, and that is the point of the decision.** A `value` component and the browse
detail mark a reading that is outside its declared range and say which side it is on. ADR-0024 §5's rule —
every component that reads a tag shows that tag's quality — extends to this: **a reading the product knows
is outside its range cannot be drawn exactly like one that is inside**.

**6. What this is not.** Not an alarm: no journal row, nothing to acknowledge, nothing to shelve. Not a
write path, and not a permission. And **not a symbol state**: a symbol derives its state from the value and
the *quality*, and quality overrides it (ADR-0027 §4) — an out-of-range reading is neither a quality change
nor a state, so a pump whose tag leaves its declared range keeps drawing exactly what it drew.

**7. Numeric tags only, for now.** A boolean, text or discrete tag has no span to declare, and the API
refuses a range on one by name rather than storing a field nothing can read. A discrete code outside the
set a device documents is the nearest non-numeric case; it belongs with the `discrete` kind's own missing
editor (`open-work.md` §2.0l), not here.

## Consequences

- **One migration**: two nullable columns, two CHECK constraints (paired, and ordered), and `tag_active`
  recreated to carry them — which drags `alarm_definition_active` down and back up with it, the cascade
  migration 0007 already documents. The null case is written out explicitly, because Postgres accepts a
  CHECK whose comparison is `unknown`: `range_low < range_high` **alone** would pass a row with one end
  null. That is the three-valued-logic trap this project has been caught by once, and it is why the paired
  constraint is a constraint of its own.

- **The migration cannot change what an existing tag means.** Every row gets null, and null produces no
  status (decision 3), so an upgrade is invisible until somebody declares a range.

- **A recreated view is not automatically readable.** Migration 0017 records the lesson — "granting on the
  table does not grant on the view over it" — and the two views recreated here are granted explicitly in
  the same migration. What a database actually holds was checked rather than assumed: on a migrated
  deployment, `alarm_definition_active` carries `SELECT, INSERT` from the default privileges 0008/0009
  established, and `tag_active` carries the wider set 0008 granted by name.

- **The tag form gains two fields, and the API refuses a bad range by name** — half of a range, ends the
  wrong way round, or a range on a tag that cannot have one. Refused rather than clamped, for the reason
  §2.0f refused an out-of-range response timeout: a silently repaired range is a range nobody chose, and
  the verdict it produces would be about somebody else's number.

- **The historian is untouched.** A range is a property of the tag's definition and is not stored with
  samples: history keeps what was measured, and a screen asking "was this plausible" asks about the tag as
  it is configured **now**. Reconstructing what a range was at the time of a reading would need the range
  in the journal's territory, and nothing has asked for that.

- **Still open, and recorded rather than implied:** whether a *deployment* should be able to declare a
  default range for a driver's tags (an OPC UA server's `EURange` arrives with the value, and this project
  does not read it yet — the OPC UA driver could carry it through, which is a smaller decision than this
  one and a real gap for a plant whose instruments already declare their ranges).

## Verified in review by

- A tag with no range produces **no range status at all** in the API, and nothing on the screen — null is
  not "in range".
- A reading above and a reading below the declared range each keep their numeric value and their reported
  quality, and the answer names which side.
- A range with one end missing, or with the low above the high, is refused with the value and the reason in
  the message; the refusal is by name, not a clamp.
- A range on a boolean, text or discrete tag is refused.
- The migration leaves every existing tag with no range and therefore no status, and the two recreated
  views are readable by the application role.
- On a screen: a `value` component bound to an out-of-range tag draws the marker and its side, and a
  `symbol` component bound to the same tag draws the same state it drew before.
