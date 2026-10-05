# ADR-0025 — An alarm waits before it announces itself, and a deadband shifts the limit

**Status:** Accepted
**Date:** 2026-10-05
**Complements:** ADR-0003 (a value carries quality and a source time, and a value with no honest
reading is not invented), ADR-0009 (soft delete via an active-row view), ADR-0013 (the alarm journal
is the source of truth and every transition is recorded in it), ADR-0015 (names unique within their
parent).

## Context

Walking Phase 5.5's gate by hand recorded this, in the phase plan, as one of the two things that
phase deliberately left open:

> **Flapping.** A value oscillating across a threshold produces a complete occurrence each time it
> crosses: during the gate, one Site wrote **three journal rows every ~25 seconds**. The journal is
> recording faithfully — the alarm really did raise and clear — but a journal that fills with an
> oscillation buries everything else in it, and an operator watching the banner learns nothing from
> it.

That is the whole problem, and the word "faithfully" is the part that makes it hard. **The engine is
not wrong.** A value crossing a limit is a real event, the journal is right to record it, and an
operator who is told "the alarm raised 340 times last night" has been told something true and
useless. ISA-18.2 calls the result a nuisance alarm and gives two standard remedies.

The engine's own shape was built for this and says so. `AlarmDefinition`'s remark, written in
Phase 3:

> Phase 3 deliberately has no hysteresis or deadband. A value hovering on a limit will chatter, which
> is a real shortcoming for a production system and a deliberate one here — the shape below takes a
> deadband later without changing.

So this ADR is the "later" that remark anticipated. What it has to decide is **which** remedy, and
the two are not interchangeable:

- **An on-delay** is a wait. The condition must hold for a configured time before the alarm is
  raised. A value that crosses for ten seconds and comes back raises nothing at all.
- **A deadband (hysteresis)** is a *different threshold for clearing than for raising*. An alarm
  raised at 4.5 bar does not clear until the value falls below 4.3 bar. It removes the chatter
  without delaying the first raise.

They look like two ways to do one thing and they are not. **An on-delay trades speed for quiet; a
deadband trades the position of a limit for quiet.** This matters more than it looks, because the
second one changes what a number on a screen means.

## Decision

**1. Both are offered, and they are separate settings, because they are separate decisions.**

`on_delay_seconds` and `deadband` are independent on an `alarm_definition`, and either may be zero or
absent. An alarm with neither behaves exactly as it does today — this ADR cannot change what an
existing alarm means, or every deployment silently acquires a different alarm system on upgrade.

**2. The on-delay is the primary remedy, and it is what "the value was out of range for less than N"
stops being an alarm at all.**

The condition must hold, continuously, for `on_delay_seconds`. A condition that stops holding inside
the window raises nothing — no journal row, no banner, nothing an operator has to dismiss again. The
alarm is raised when the wait ends, and its `SourceTimeUtc` is the sample that **confirmed** the
breach, not the sample that began it and not the moment the wait ended. A reader comparing the alarm
against the historian finds the reading that caused it.

**3. The deadband applies to clearing only, never to raising.**

An alarm still raises at exactly its limit, so an operator reading "High limit 4.50 bar" is reading
the truth about when it will go off. It clears when the value has come back past the limit by
`deadband`. **This is the direction that keeps a limit meaning what it says**, and it is why
`Breach` takes the deadband and a cleared alarm separately rather than one threshold.

The opposite arrangement — raise late, clear at the limit — is not built and is not configurable,
because it would make the configured limit a number the alarm does not actually use.

**4. A pending breach is not journalled, and that is deliberate.**

The wait is state the *engine* holds, not something that happened at the plant, and the journal
records the second. Writing an "evaluating, might raise" row would fill the journal with the same
noise this ADR exists to remove, one level up.

The cost is stated rather than hidden: **a restart resets every pending wait.** A breach that was 40
seconds into a 60-second wait when the Gateway stopped needs another 60 seconds afterwards. The
alternative — persisting pending breaches — would mean the journal carrying engine state, which is
the thing decision 4 refuses, and it would have to answer unanswerable questions about a wait that
spanned an outage in which nothing was measured.

**5. The wait is measured on the Gateway's clock, from when the breach was first seen.**

Not from the source timestamp. An on-delay is a property of the *evaluation*, and the Gateway is what
evaluates. Measuring from the source time would let a device with a lagging clock, or a batch of
buffered samples arriving at once, satisfy a 60-second wait instantly — which is exactly the alarm
arriving before anyone could have reacted to it.

**6. A value that is not Good abandons the wait rather than pausing it, and the next Good reading starts a new one.**

ADR-0003: a Bad reading has no value to compare, so it is not an evaluation. A standing alarm is
unaffected — **a device going offline must not look like the value returning to normal**, which is
the rule the engine already follows and this decision extends to a wait.

But a wait is not a standing alarm, and "paused" was the wrong answer, found by writing the test for
it. **Pausing would have the Gateway counting time in which it was not watching**: a device offline
for ten minutes would satisfy a sixty-second delay while nothing at all was measured, and the alarm
would raise on the strength of an outage. The condition is *the value has been out of range for N
seconds*, and a silent device is not a device holding a condition. So the wait starts again from the
first Good reading after the gap, and an alarm that was about to raise raises once readings resume —
later than it would have, and honestly rather than on time for the wrong reason.

**7. Changing either setting does not reach back into a wait already in progress.**

A wait that was started under one on-delay finishes under it. The alternative — recomputing every
pending wait on every catalogue change — would mean an unrelated edit elsewhere on the Site silently
cancels or fires an alarm, which is the class of surprise ADR-0019 §5 exists to refuse for edge
configurations and which applies here for the same reason.

## Consequences

**What becomes possible.** An alarm can be configured so that a value which dips across a limit for a
few seconds and recovers is not an alarm, and so that one which hovers on a limit raises once instead
of forty times. The journal becomes a record of excursions rather than of instants.

**What it costs, and the first one is the real one.**

- **An alarm is late by `on_delay_seconds`.** That is the entire mechanism and it cannot be avoided by
  choosing a better implementation. A deployment that needs an alarm the instant a limit is crossed
  sets the delay to zero and accepts the chatter, and that is a legitimate answer — which is why the
  column is nullable rather than defaulted to something.
- **A pending wait is lost on restart** (decision 4). On a Gateway that is restarted often, an alarm
  with a long delay may take noticeably longer to appear than its delay suggests.
- **The deadband makes "in alarm" and "back in range" different numbers** (decision 3), which a reader
  has to know. A value sitting between the two is in range and the alarm has not cleared, and a screen
  showing "4.45 bar, High limit 4.50" beside a standing alarm is not a contradiction.
- **Both settings are per definition** (ADR-0015's table), so an alarm that should behave the same on
  three devices needs three definitions configured the same way. That is the existing shape of alarm
  configuration and this ADR does not change it.

**What is deliberately not built.**

- **No debounce on clearing.** A clear-delay would postpone the journal row without removing it, and
  the journal would still fill — the wrong tool for this problem.
- **No rate-of-change or adaptive threshold.** Both are real techniques and neither is needed by the
  problem as recorded.
- **No per-tag or per-Site default.** A default would mean an alarm definition's behaviour depending
  on something not on the definition, and "why did this one not raise" would stop being answerable
  from the alarm's own row.

**How this is proven.** The on-delay's absence-of-a-raise is a fact about something *not* happening,
which is the hardest kind of thing to test and the reason both halves are pinned: a condition that
holds for less than the delay raises nothing **and** leaves the journal unchanged, against a control
with no delay that raises immediately. The deadband is two tests, because its whole content is that
the raise and clear thresholds are different: it raises at the limit, and it does not clear until the
value is past it by the band.
