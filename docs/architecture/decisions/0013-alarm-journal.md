# ADR-0013 — Alarm journal: an append-only event log, with the live list derived from it

**Status:** Accepted
**Date:** 2026-09-18

## Context

Phase 3 shipped a working alarm engine whose state lives entirely in
memory. Restarting the Gateway erases every standing alarm: an
unacknowledged excursion nobody has seen yet simply disappears, and so
does the record that it happened. That was flagged when Phase 3 closed
and deliberately left open; it has to be settled before this platform
runs anywhere real, because a restart is an ordinary event — a
deployment, a container reschedule, a server reboot — and an alarm
system that forgets across one is not an alarm system.

Two different needs hide inside "persist the alarms", and conflating
them produces the wrong schema:

- **Survive a restart.** Whatever was standing must still be standing
  afterwards, in the same state, with the acknowledgement that was
  already given.
- **Answer questions afterwards.** What alarmed last Tuesday, when, for
  how long, who acknowledged it, and was anyone even watching. That is
  process history, and it outlives the alarm itself in exactly the way
  ADR-0001 already says historised data outlives its configuration.

A third question only appears once the first two are taken seriously:
**an alarm engine that isn't running isn't evaluating.** A journal that
records only alarms makes an outage indistinguishable from a quiet
period — a reviewer reading continuous silence concludes nothing
happened, when in fact nothing was being watched. That is the same class
of mistake this project has now corrected three times (a driver
fabricating a value for a Bad reading, a chart drawing a line through a
gap, a hidden Site answering 403): inferred absence presented as
observed absence.

## Decision

**An append-only `alarm_event` table is the source of truth, and the
live alarm list is derived from it.** There is no second table holding
"current alarms". On startup the engine rebuilds its in-memory list by
reading the open events; from then on it appends an event for every
transition and keeps its memory in step. One place to write, one place
to read from, nothing to drift — the same reasoning that put the
active-row filter in a view rather than in every query (ADR-0009).

An event records: the alarm definition and tag it belongs to, the **Site
denormalised onto the row**, the event type and when it occurred, the
acting user where there was one, the limit and the value that triggered
it, and the tag's display path *as it read at that moment*. Site is
copied onto the event rather than resolved through tag → device → site
at query time because Phase 5 already proved that path breaks for a
soft-deleted tag, and alarm history for retired equipment is exactly
what a review needs. The path is snapshotted for the same reason it is
never an identity (ADR-0001): it is mutable, and a journal should show
what the operator actually saw.

**Event types** are `Raised`, `Acknowledged`, `Shelved`, `Unshelved`,
`Cleared` and `Retired`, plus two that are about the engine rather than
any one alarm: `EvaluationStarted` and `EvaluationStopped`. Those two
are what let a reader tell silence from blindness. A clean shutdown
writes `EvaluationStopped`; a start always writes `EvaluationStarted`,
so an unclean stop shows up as a start with no matching stop — which is
itself the honest record of a crash.

**A clear found on the first evaluation after a restart is marked as
such.** The engine did not observe the value returning to range; it
observed that the value is in range now. The event carries the
observation time and a flag saying it was detected after a restart,
rather than implying the plant recovered at that instant. Whoever reads
the journal later gets "we noticed at 08:31, having not been watching
since 03:12" instead of a fabricated recovery time.

**Shelving gains an expiry.** `ShelvedUntilUtc` is required, capped at a
configurable maximum (default 24 hours). When the shelf expires and the
value is still out of range, the alarm returns as `Active` and an
`Unshelved` event is written — it re-announces itself, which is the
entire point of a timer. Until now an indefinite shelf was survivable
only because a restart happened to undo it; once shelving persists, that
accidental safety net disappears, and a suppression made at 3am could
otherwise hide an alarm for good with nobody aware it was ever set.

**The journal and the audit log both record an acknowledgement, on
purpose.** `audit_log` (ADR-0011) answers "who did what to this system"
and belongs to security; `alarm_event` answers "what happened at the
plant" and belongs to process history — a reviewer with no audit access
must still be able to read a complete alarm story. They are not derived
from one another and neither is redundant; do not "de-duplicate" them
later.

`alarm_event` is append-only **at the database level**, exactly as
`audit_log` is: the application role holds `INSERT` and `SELECT` and
nothing else. As with the audit table, a test that proves this must run
over the application's own connection — under a superuser it proves
nothing.

It is a plain table, not a hypertable. Alarm events are orders of
magnitude sparser than samples, and nothing here needs TimescaleDB's
partitioning; retention for the journal is left untouched until a real
deployment gives a reason to set one.

## Consequences

A restart stops being a silent amnesia event, and the journal answers
questions the live list never could — including "was anyone watching",
which most alarm systems cannot answer at all. The engine gains a
startup path it did not have, and a dependency on persistence that Core
must express as an abstraction rather than a concrete store (ADR-0002).

Deriving the live list from events rather than storing it means startup
does real work before the first scan, and that work grows with the
number of *open* alarms, not with history. Shelve becoming time-bounded
takes away an operator's ability to suppress something indefinitely in
one action; that is the intent, and a genuinely permanent suppression is
a configuration change (removing or raising the threshold), which is an
Admin action with an audit trail — which is where a decision that
lasting belongs.

## Verified in review by

- An alarm standing before a Gateway restart is still listed after it,
  in the same state, and an acknowledgement given before the restart is
  still attached to it afterwards, naming the same user.
- A clear that is first observed after a restart is recorded with the
  observation time and marked as detected-after-restart, never with a
  time implying the value recovered while nothing was running.
- The journal shows the period during which no evaluation was running:
  a start event with no preceding stop event after an unclean shutdown.
- A shelved alarm whose shelf expires while the value is still out of
  range returns to `Active` and writes an `Unshelved` event, with no
  operator action.
- A shelve request beyond the configured maximum is rejected.
- Alarm history for a soft-deleted tag is still readable and still
  filtered to the caller's permitted Sites (ADR-0011) — the Site on the
  event row is what makes that possible.
- `alarm_event` refuses `UPDATE` and `DELETE` executed over the
  application's own connection; a test that runs as a superuser does not
  satisfy this row.
