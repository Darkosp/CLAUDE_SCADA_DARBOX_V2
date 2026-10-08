# ADR-0034 — An alarm carries a priority, and an alarm nobody has rationalised says so

**Status:** Accepted
**Date:** 2026-10-08

## Context

An `AlarmDefinition` carries limits, an on-delay and a deadband (ADR-0025), and **no priority or
class**. Every standing alarm is therefore equal to every other, which means:

- the standing list and the journal can only sort by **time**, so the most recent thing is the most
  prominent thing whatever it is;
- there is nothing to sort *by* during a flood, which is the one moment the ordering matters;
- escalation and notification have nothing to key on, which is why ADR-0013 and the still-unscoped
  notification work both stop at the same wall;
- and the standard's own measure of whether an alarm system works cannot be computed at all.

`standards-baseline.md` ranks this third of its biggest gaps, and **the same field is missing against a
second, independent standard** — OPC UA Part 9 carries a `Severity` on a condition (§4.8, added
2026-10-08). A field two unrelated standards both require is not a matter of taste.

### What the standards say, and what they deliberately do not

**ISA-18.2 / IEC 62682** make priority a product of *rationalisation*: it is set deliberately, per
alarm, from the **consequence** of ignoring it and the **time the operator has to respond**
([EEMUA's definition](https://eemua.org/glossary/a/alarm-priority) is "the ranking of alarms by
severity and response time"). IEC 62682:2022 is the international edition, and it turned many of
ISA-18.2's recommendations into requirements
([exida](https://www.exida.com/Blog/alarm-management-goes-global-with-the-release-of-iec-62682)).

The standards **do not prescribe a matrix, a set of labels, or response-time cut-offs**. Those belong
to a site's *alarm philosophy*. What they do give is a number: a recommended **annunciated priority
distribution of roughly 5% high, 15% medium, 80% low**
([exida on which distribution the metric applies to](https://exida.com/Blog/which-measure-rationalized-or-annunciated-is-more-important),
citing ISA-18.2 §16.4.9). The reason is stated plainly in the same source: *if too many alarms are high
priority, effectively none of them stand out.*

**None of the standards' text was read** — all three are paywalled, and the above is from the public
secondary sources named. No clause is quoted that was not quoted by one of them.

## Decision

**1. An alarm definition carries a priority from a fixed, ordered set of three: High, Medium, Low.**

A small fixed set rather than a number, and three rather than five, for reasons that come from the
standard rather than from taste:

- **The distribution target only means something with countable buckets.** "5% high, 15% medium, 80%
  low" is the one concrete, checkable thing ISA-18.2 offers, and it is a statement about three groups.
  A free integer cannot be measured against it without someone inventing the groups afterwards.
- **An operator cannot act on "severity 673".** Priority exists to answer *which of these do I deal
  with first*, and a scale finer than the number of distinguishable responses is a scale that has to be
  re-collapsed in the reader's head.
- **A free integer gets a different scale per engineer.** The predictable end state is forty distinct
  priorities, which is the same as none — the failure the distribution target exists to prevent, by a
  different route.

**A fourth tier is a later ADR and costs almost nothing.** Some sites put an *Emergency* or *Critical*
level above High. The model here is an ordered enum and a sort; adding a member changes neither. It is
not added now because nothing has asked for it, and because three is the set the published distribution
target is stated for.

**2. Null is a real state, and it is the standard's own: *not yet rationalised*.**

An alarm definition's priority is **nullable**, and null does not mean *low*, *unknown* or *default*.
It means **this alarm has not been through rationalisation**, which is a first-class condition in
ISA-18.2's own lifecycle rather than an absence in ours.

This is the third time this project has had to say it — ADR-0025's null on-delay, ADR-0030's undeclared
range, and now this — and it is the same rule: **null is not zero**. Here it buys something specific:
the migration adding this column **does not have to guess**. Defaulting every existing alarm to High
would make the system 100% high priority, which the standard says is the same as having no priorities.
Defaulting them all to Low would silently downgrade something that matters. Both are the product
deciding something only the plant can decide.

**An unrationalised alarm sorts last, and is visible as unrationalised** rather than being hidden among
the Lows. A reader must be able to find them, because working through them is the job.

**3. Priority is a property of the definition and never changes with state.**

It does not rise because an alarm has been standing a long time, and it is not affected by
acknowledgement, shelving, or the value. ADR-0025 owns when an alarm raises and clears; ADR-0013 owns
its states. **Priority is a statement about consequence, which does not change because time passed.**

Escalation — doing something more when a high-priority alarm stays unacknowledged — is a real feature
and is **not this ADR**. It needs the notification decision `phase-0-architecture.md` still lists as
open, and it needs this field to exist first.

**4. What priority changes today: ordering, and nothing else.**

The standing list and the journal sort by **priority, then by time within a priority**. That is the
whole of the behaviour change, and it is deliberate that it is small: a field that nothing reads is a
column, and a field that six things read is a decision nobody can revisit.

Specifically **not** decided here, each because it needs something else first:

- **Colour or any other visual weight by priority.** `standards-baseline.md` §4.7 has just recorded
  that **red is already doing two jobs** in this product — Bad quality and an active alarm, in two
  near-identical reds — and IEC 60073 reserves red for the condition demanding action. Giving priority
  its own colours before that is settled would be adding a third claim to a channel that already
  carries two. It is an ADR of its own, and this one must not pre-empt it.
- **Annunciated distribution reporting.** The standard's own measure, and genuinely worth building —
  but it is a report, this project has no reporting, and a number nobody can see is not a measure.
  Recorded in `open-work.md` as what this field unlocks.
- **Sound, flashing, or an alarm banner.** ISA-101's territory and already listed as missing there.

**5. The vocabularies are reconciled once, here, rather than per-feature.**

OPC UA Part 9 carries a numeric `Severity`; ISA-18.2 carries named priorities. **The named set is this
product's model**, and a numeric severity is a *mapping at the edge* for anything that has to speak OPC
UA — not a second stored field. Two stored representations of one fact drift, which is the defect shape
this repository has met as *a rule and every place that applies it have to move together*.

The mapping is stated now so that a later OPC UA alarm feature does not invent its own: **High → 700,
Medium → 500, Low → 300**, inside Part 9's 1–1000 range, with gaps so a fourth tier fits without
renumbering. Nothing uses this yet; it exists so that the first thing that needs it does not decide it
alone.

## Consequences

- **Every existing alarm definition becomes "not yet rationalised"**, including the demo's. That is
  accurate rather than convenient, and it is the only answer that does not have the product inventing a
  consequence assessment it is not entitled to make.
- **The standing list's order changes** for any deployment that sets priorities, and does not change
  for one that sets none — because everything sorts equal and falls back to time, which is today's
  behaviour exactly.
- **The schema gains one nullable column** and the API one optional field. A client that does not send
  it leaves the alarm unrationalised, which is the correct reading of silence.
- **It unblocks two things rather than one**: escalation (with the notification decision) and the
  distribution metric (with reporting). Neither is built here.
- **The audit trail covers the change already** — setting a priority is an alarm-definition edit, and
  those are audited. No new action is added, for the same reason ADR-0026 dropped
  `tag.write_from_screen`: the distinction available would be which screen somebody was looking at.

## Alternatives considered

**A numeric severity, 1–1000, as OPC UA has it.** Refused by decision 1. It is the right thing on a
wire and the wrong thing in a user interface, and this product's model is what an operator configures.
The mapping in decision 5 keeps the wire reachable without storing it twice.

**Making priority required, with a default for existing rows.** Refused by decision 2, and it was the
tempting option because it avoids a nullable column. Every available default is a lie: High makes
everything high, Low silently downgrades, Medium asserts a middling consequence for alarms nobody has
assessed. Phase 6.5's migration renamed a duplicate that was *really there*; there is no equivalent
here, because the right value is not knowable from the data.

**Four or five priorities.** Refused for now by decision 1, and cheaply reversible. Three is the set
the published distribution target is stated for, and a tier nobody has asked for is a tier every
rationalisation session has to argue about.

**Deriving priority from the limit's distance, or from the tag's declared range (ADR-0030).** Refused,
and it is worth saying why because it looks clever. Consequence is not a function of magnitude: a
small deviation on a safety-critical tag outranks a large one on a buffer tank, and the whole point of
rationalisation is that **a person decides this**. A derived priority would be the product asserting it
knows the plant.

## Verified in review by

- A definition saved with no priority reads back as null, and is reported as **not yet rationalised**
  rather than as Low — asserted as its own test, because this is the case that fails silently.
- The migration sets **no** priority on any existing row, and a test fails if it does.
- Standing alarms sort High before Medium before Low, and unrationalised **last**; within one priority,
  by time, as they do today.
- A deployment that sets no priorities anywhere gets exactly today's ordering — a control test, so the
  change is shown to do nothing where nothing was asked for.
- An unrecognised priority on the wire is refused by name, with nothing stored (the shape ADR-0021's
  version 3 refusal takes).
- Priority is unchanged by acknowledging, shelving, clearing and re-raising an alarm — one test per
  transition, because decision 3 is the kind of rule a later feature erodes by accident.
- **Nothing here is believed until the list has been looked at.** The ordering is the whole feature and
  it is a visual claim; ADR-0031 and ADR-0033 both shipped correct and unwalked.
