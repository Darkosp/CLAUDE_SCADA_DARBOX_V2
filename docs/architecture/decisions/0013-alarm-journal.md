# ADR-0013 — Alarm journal: an append-only event log, with the live list derived from it

**Status:** Accepted
**Date:** 2026-09-18 (extended the same day, before any implementation
existed — see "Resolved during pre-implementation review")

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

An event records: the **occurrence** it belongs to (see below), the
alarm definition and tag, the **Site denormalised onto the row**, the
event type, **two timestamps**, the acting user where there was one, the
limit and the value that triggered it, and the tag's display path *as it
read at that moment*. Site is
copied onto the event rather than resolved through tag → device → site
at query time because Phase 5 already proved that path breaks for a
soft-deleted tag, and alarm history for retired equipment is exactly
what a review needs. The path is snapshotted for the same reason it is
never an identity (ADR-0001): it is mutable, and a journal should show
what the operator actually saw. The acting user is kept the same way —
the id, which is stable, **and a snapshot of the name as it then read**,
because users get renamed and deactivated, and a Viewer reading the
journal cannot resolve an id (the user list is Admin-only, ADR-0011).

**An occurrence, not a definition, is what an event belongs to.** One
definition raises many separate alarms over the life of a plant; keying
events only by definition makes it impossible to pair a `Raised` with
its ending, to ask how long an alarm lasted, or even to define which
events are still open. Every event therefore carries an
`OccurrenceId`: a `Raised` starts one, every later event for that alarm
repeats it, and `Retired` closes it. "Open" means an occurrence with no
`Retired`.

**A new breach after an unacknowledged clear starts a new occurrence.**
The engine kept one alarm per definition and ignored a fresh breach
while a `Cleared` alarm was still listed, so the live list went on
saying "recovered" while the value was out of range again — true about
the past, wrong about the present, which is what an operator is actually
looking at. A breach now retires the cleared occurrence, recording that
a new one superseded it, and raises a new occurrence with its own id.
Nothing is lost by retiring it: the journal is where that excursion now
lives, and being able to tell the live list the truth about *now* is
precisely what having a journal buys.

**Two clocks, both kept, following `tag_sample`'s precedent.** A
value-driven event carries the reading's source timestamp (ADR-0003) and
the time the engine actually recorded it. For OPC UA those differ
routinely — a server may hand over a value that changed minutes or hours
earlier — and collapsing them would recreate, in the journal, the exact
confusion ADR-0003 exists to prevent. Engine-level events have no source
timestamp at all, which is honest: nothing at the plant produced them.

**Event types** are `Raised`, `Acknowledged`, `Shelved`, `Unshelved`,
`Cleared` and `Retired`, plus two that are about the engine rather than
any one alarm: `EvaluationStarted` and `EvaluationStopped`. Those two
are what let a reader tell silence from blindness. A clean shutdown
writes `EvaluationStopped`; a start always writes `EvaluationStarted`,
so an unclean stop shows up as a start with no matching stop — which is
itself the honest record of a crash.

**Anything first seen on the first evaluation after a restart is marked
as such — in both directions.** A clear found then was not observed
happening: the engine observed that the value *is* in range now. A
breach found then is very likely an alarm that was raised during the
outage, not one that began at that instant. Both carry the flag, so a
reader gets "noticed at 08:31, having not been watching since 03:12"
rather than a fabricated time in either direction. The original text
covered only the clear; the raise has the same problem and takes the
same treatment.

**Shelving gains an expiry.** `ShelvedUntilUtc` is required, capped at a
configurable maximum (default 24 hours). When the shelf expires the
alarm returns as `Active` and an `Unshelved` event is written — it
re-announces itself, which is the entire point of a timer. Until now an
indefinite shelf was survivable only because a restart happened to undo
it; once shelving persists, that accidental safety net disappears, and a
suppression made at 3am could otherwise hide an alarm for good with
nobody aware it was ever set.

Three things follow from that and are decided here rather than left to
the implementation. The expiry needs **a clock the engine does not have
today**: it only acts when a value arrives, so an alarm on a device that
has gone offline — no readings at all — would never unshelve. A periodic
sweep drives it, the same shape as the session sweep already built in
Phase 5. **An expiry that falls while the value is unknown still
unshelves**, returning the alarm to `Active`: unknown is not the same as
fine, and the failure to prefer here is the loud one. And the engine
does **not** remember what state an alarm held before it was shelved, so
one that had been acknowledged comes back needing acknowledgement again.
That is intended, not an oversight: re-acknowledging is the price of
having hidden it.

**The alarm engine learns who acted, and that is a deliberate reversal.**
`IAlarmEngine` currently documents that it records "when, not who", so
that the engine stays free of any notion of users and the caller writes
the audit entry (ADR-0011). That was right while the engine held only
live state; it stops being right the moment the journal has to answer
"who acknowledged this" after a restart, from its own rows. Acknowledge
and shelve therefore take the acting user. Core already models users
(`Core/Security`), so nothing about ADR-0002's boundary is strained —
but the code comment stating the opposite must be replaced rather than
left to read as though this were drift.

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

**Grants are opt-in from here on.** Migration 0008 set default
privileges so that every table created afterwards grants the application
role `SELECT`, `INSERT`, `UPDATE` and `DELETE`, with `audit_log`'s
append-only posture restored by revoking afterwards. `alarm_event` would
be born writable the same way. That polarity is backwards for this
project: forgetting to revoke destroys a guarantee silently, while
forgetting to grant fails loudly the first time anything writes. The
default becomes `SELECT`/`INSERT` only, and a table that genuinely needs
`UPDATE`/`DELETE` — which most configuration tables do — grants them
explicitly in the migration that creates it. Existing tables keep the
grants they already have; default privileges only affect tables created
later.

## Resolved during pre-implementation review (2026-09-18)

Reading this ADR against `AlarmEngine`, `TagEngine`, `IAuditLog` and
migration 0008 before writing any code raised four questions this text
did not answer. They are settled here rather than inside the
implementation, and no code had been written against the earlier text.

**Alarm evaluation stops silently when the database is down, today.**
`TagEngine.IngestAsync` writes the historian before notifying the alarm
engine, so a failed historian write skips alarm evaluation for the whole
batch while the scanner logs only "Scan failed". Nothing is being
watched, the system looks healthy, and the journal cannot record the gap
because the database is what is missing. That makes this ADR's central
promise — that the journal tells you when nobody was watching — hollow
in exactly the case it matters most. **Alarm evaluation must not be
downstream of the historian write succeeding.** Fixing that is Phase 1
code and belongs to this phase anyway, because the guarantee being built
here depends on it.

**When a journal write fails, what the engine does depends on who asked.
** An operator action — acknowledge, shelve — writes its event first and
**fails the request if the event does not persist**: an acknowledgement
nobody can later prove happened is worse than a button that reports it
did not work. A value-driven transition does the opposite: the live list
and the banner update **even if the journal write fails**, because an
operator standing in front of a screen needs to see the alarm more than
the database needs to have recorded it. Once journal writes succeed
again, the engine appends an event recording the window during which
journalling was failing — the same principle as the evaluation gap, so a
later reader is never handed a record with silent holes in it.

That event is named `JournalGap`, and it is deliberately not shaped like
`EvaluationStarted`/`EvaluationStopped`. Those come in pairs because the
engine is alive at both ends and can write both. A journal failure
cannot be recorded at the moment it begins — the writing is precisely
what is missing — so this is a single retrospective row describing a
window that has already closed: it carries its own `from` and `until`
rather than being paired with anything, and it carries **how many
transitions went unrecorded** in that window. A reader told "three
transitions were not recorded between 04:12 and 04:19" can act on
that; one told only that a gap existed cannot.

**An alarm whose definition is deleted is retired automatically**, with
an event saying that is why. Nothing evaluates a removed definition, so
such an alarm can never clear on its own; in memory it merely lingered
until an Admin acknowledged it, but once the live list is rebuilt from
the journal it would return at every startup, forever. Retiring it is
the only ending that terminates, and recording the cause keeps it
distinguishable from an operator's acknowledgement or a real recovery.

**After an unclean stop, the last-alive time comes from the historian,
not from a heartbeat.** The journal only has rows at transitions, so
after a crash its last event may be hours before the Gateway actually
died, and "not watching since 03:12" would be wrong. The historian's
most recent `ingested_at` before the restart already bounds when the
Gateway was last alive, at no cost; `EvaluationStarted` records that
bound. Periodic heartbeat events would buy the same thing by writing
rows forever to cover a rare case.

**Engine-level events carry no Site, and must survive the Site filter.**
`EvaluationStarted` and `EvaluationStopped` belong to no Site and are
visible to every signed-in user: they expose nothing Site-specific, and
a Viewer on one Site still needs to know the system was not watching.
This is a trap rather than a preference — a filter written as
`WHERE site_id = ANY(...)` silently drops every row whose Site is null,
which is precisely the class of quiet, wrong answer this project keeps
finding. It needs a test, not just a sentence.

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
- A value that recovers without being acknowledged and then breaches
  again produces a second occurrence with a different id, and the live
  list shows the alarm as Active rather than Cleared.
- Alarm history for a soft-deleted tag is still readable and still
  filtered to the caller's permitted Sites (ADR-0011) — the Site on the
  event row is what makes that possible.
- `alarm_event` refuses `UPDATE` and `DELETE` executed over the
  application's own connection; a test that runs as a superuser does not
  satisfy this row.
- Every event of one alarm shares an `OccurrenceId`, and a second
  occurrence on the same definition gets a different one — so "how long
  did it last" and "which are still open" are answerable from the table
  alone.
- A failed historian write does not stop alarms being evaluated: with
  the historian made to fail, a breaching value still raises an alarm
  and still reaches the banner.
- An operator's acknowledgement fails, and says so, when its journal
  event cannot be written; a value-driven raise still reaches the live
  list under the same failure, and the window of failed journalling is
  recorded once writes succeed again.
- An alarm whose definition is deleted does not reappear after a
  restart, and its retirement names the deletion as the cause.
- `EvaluationStarted` records a last-alive bound taken from the
  historian after an unclean stop.
- A Viewer scoped to one Site sees `EvaluationStarted`/`EvaluationStopped`
  in the journal — the Site filter does not drop rows whose Site is
  null.
- A shelved alarm on a device that has gone offline — no readings at all
  — still unshelves when its shelf expires.
