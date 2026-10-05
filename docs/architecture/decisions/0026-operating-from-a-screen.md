# ADR-0026 — Operating from a screen: one component writes, and nothing else does

**Status:** Accepted
**Date:** 2026-10-05

## Context

ADR-0024 built operator screens and stopped one step short of acting on them. Its decision 8 says so
explicitly:

> A writable tag is marked writable on a screen; acting on it is not built here. Writing from a screen
> puts the Operator check, the audit entry and the write path's own refusals behind a button rather
> than a form, and that is a decision about a screen rather than about storage — so it is the next
> slice's, and this ADR does not half-take it.

Everything the write path needs already exists and is not this ADR's to change. `POST
/api/tags/{tagId}/value` reads the tag, resolves its device, answers 404 for a Site the caller cannot
see, 403 without the Operator role, 400 for a tag that is not writable or for a value of the wrong
kind, and 502 when the device refuses it. It records `tag.write` on success and `tag.write_failed` on
failure, because an attempt may have reached the equipment even when it did not report success. Where
the device is read by an edge, ADR-0023 routes the write over the link and refuses to report it done
before it is.

What is missing is the screen. And a screen is not a form: the API is reached by something that can
send JSON and read a status code, whereas an operator is standing in front of a plant, possibly holding
a tablet, and the difference between those two situations is where this ADR's decisions are.

Two things make this the most consequential decision the project has taken after ADR-0023. **It is the
second thing that lets this software change a plant from a web page**, and it is the first that an
operator reaches by pressing a control that sits beside the value it acts on — which is precisely what
makes misreading it easy.

## Decision

**1. Only a `value` component can initiate a write.**

A `value` component is bound to one tag and shows that tag's reading. It is the only kind whose whole
purpose is "this number, here", so it is the only one where a write control cannot be mistaken for
something else. `status`, `trend` and `alarms` keep the readable marker ADR-0024 requires and gain
nothing to press.

The alternative — a capability flag on every kind — was rejected because it makes the question "can
this be written" a property of the screen rather than of the tag and the reader, and because a trend
with a write button is a button whose subject is a *series*. `label` and `alarms` read no tag at all.

**2. The control is offered only when the caller could actually use it, and the server decides that —
as it already does for `readable`.**

`ScreenComponentDto.writable` is already computed server-side (ADR-0024 §5, extended in Phase 8's walk)
and is false when the tag is not writable, when the reader may not see it, or when the reader cannot
operate the Site. The client draws the control from that flag and does no permission reasoning of its
own.

This is not belt-and-braces. The API refuses an unpermitted write regardless of what any client draws,
so the flag is not enforcement; it is **the difference between a control that is refused and a control
that should not have been offered**. An operator who is shown a button and then told no has been told
something about the system that was not true a moment earlier.

**3. The write is confirmed in a dialog, and the dialog does not close until the server has answered.**

Pressing the control opens a form showing the tag's path, its **current reading with its quality and
its source time**, and a field for the new value in the tag's unit. Submitting is an explicit press of
`Write`. **`Enter` in the field submits, because this is a form and that is what a form does** — a
control that ignored `Enter` would be a control that behaved unlike every other form the operator has
used. `Escape` does not submit; it closes without writing, which is the safe direction for a stray
keystroke.

A boolean tag gets a two-option picker rather than a text field, because there are two answers and
neither is a typo away. The dialog is not closable by clicking its backdrop: the backdrop click is the
gesture most likely to be accidental, and this is the one dialog in the product whose accidental
dismissal would be followed by the operator wondering whether the write happened.

While the request is in flight the dialog is disabled. **This is about double-writing, and it is not
theoretical**: a slow device plus an impatient operator is a second command sent to a plant because
the first appeared not to work. A short write is idempotent in most plants and a toggling or
incrementing one is not, which is the same argument ADR-0023 uses for refusing to retry a write.

**4. The current reading, its quality and its age are shown in the dialog, and a Bad or stale reading
does not prevent the write.**

An operator about to change a plant should be looking at what the plant is doing. The dialog shows what
the screen last received, and where that is not Good, or the source time is old, it says so.

It does **not** refuse the write. There is a real case for writing to a device whose reading is Bad —
a controller that has stopped reporting is exactly what someone may need to nudge — and a rule that
refused would be this project deciding a plant procedure it cannot see. Showing the age and letting the
operator decide is the honest version: the information is what was missing, not a lock.

**5. A write is never held, queued or replayed, and its success is never reported before the answer.**

ADR-0023's rule, restated here because the screen is a new way to reach it and the temptation is new:
the operator is waiting, and telling them it worked so the dialog can close more smoothly would be
this software making a claim about a plant it has not earned. A failure keeps the dialog **open with
the value still in it**, so the operator can correct a typo or press Write again, and the server's own
message is shown rather than a generic one.

**6. A write does not acknowledge an alarm, and the screen does not offer to.**

Writing a value into the range that clears an alarm will clear it, and that is the plant's doing rather
than the operator's statement. Acknowledgement is a claim that a human has seen an alarm (ADR-0013) and
it stays where it is, on the alarm.

**7. No new switch, and the write capability is unchanged.**

This ADR adds **no deployment setting**. An Operator could already write any writable tag through
`POST /api/tags/{tagId}/value` before this ADR, and still can; what changes is that the value and the
control that acts on it now appear together. That is an affordance, not a capability.

The alternative — a `ScreenWrites:Enabled` switch, off unless a deployment turns it on — was
considered and rejected on two grounds, and the second decided it. It would be a switch whose off
position does not remove the ability, only hides one of the two ways to reach it, which invites a
deployment to believe it has disabled something it has not. And it would contradict the pattern the
project already chose for the neighbouring question: ADR-0023's `EdgeProvisioning:WritesEnabled`
defaults to **on**, with the comment that an Operator "may write one and always could". Adding the
first write switch as an off-by-default while the second is on-by-default would say two different
things about the same capability.

What protects the plant is what already did: the tag must be writable, the caller must be an Operator
on its Site, the write is not held, and where the device is behind an edge
`EdgeProvisioning:WritesEnabled` still applies.

**The mis-press concern is real and is answered by the dialog, not by a switch** — decisions 3 and 5
are where that argument lives. A deployment that wants no writes at all has a task for
`EdgeProvisioning:WritesEnabled` today only for edge devices; making a tag not writable is the answer
for the rest, and it is per tag rather than per deployment.

## Consequences

- **The write path is unchanged, and that is the point.** This ADR adds no endpoint, no permission
  rule, no audit action for the write itself and no new way for a device to be reached. If any of those
  had been needed, it would have been a sign that the earlier decisions were wrong.

- **No audit action is added, and the first draft of this ADR added one.** It proposed
  `tag.write_from_screen` beside `tag.write`, so a reviewer could tell an operator at a screen from a
  script calling the API. Reading the code refused it: **the Browse view already has a write form**, so
  writes through the API were already coming from people at screens, and a `_from_screen` action would
  have split the log by *which page* the person was looking at while leaving the other interactive path
  unmarked. The distinction that was available is not the distinction the question needs. What the
  record already answers — who, what tag, what value, what the device said — is what a review asks.

- **A screen becomes able to change a plant, and every screen an operator sees is one an Admin wrote.**
  That is worth saying plainly, because it means the set of things an operator can press is under an
  Admin's control rather than a fixed surface. It is also the honest description of every HMI: the
  question is not whether the screen can change the plant but whether anyone has decided what it may
  change.

- **Missing from the on-premises guide until now:** nothing, because nothing is configurable. A
  deployment that wants a tag unwritable changes the tag, and a deployment that wants no writes over
  an edge link already has `EdgeProvisioning:WritesEnabled`.

- **`writable` stops being a decoration.** It was added by the Phase 8 walk so that ADR-0024 §9's
  requirement held while acting was unbuilt, and its marker carried the note "from a screen, not yet".
  That note is now false and has been removed. The flag's remaining job is what decision 2 says: the
  control exists where the flag is true.

- **No confirmation step, no two-person rule, no typed confirmation.** For a project whose only other
  ability to change a plant is an API call, an explicit button with the current reading beside it is
  proportionate, and a plant that needs more has its own interlocks. This is recorded as a decision so
  that it is not mistaken later for an oversight — and so that a deployment needing more knows it is
  asking for something this ADR did not provide.

- **Still not decided.** Whether a screen can hold a setpoint that is written on a button press as a
  group; whether a write can be scheduled; whether there is a notion of a screen being "in control"
  that a second operator can see. None is ruled out by this ADR and none is implied by it.

## Verified in review by

- Reading `TagWriteEndpoints.MapTagWriteApi` against decisions 2 and 5 and confirming that this ADR
  added nothing to it.
- Confirming that `writable` is still computed from the tag, the reader's visibility and the reader's
  Operator role, and that no client decision was introduced beside it.
- Checking that a failed write leaves the dialog open with its value, by fault rather than by reading:
  the test drives a device that refuses the write.
- Confirming that an `alarms` component and a `trend` component expose no control, and that a `value`
  component whose `writable` is false exposes none either.
- Confirming that no deployment setting was added, by reading `GatewayApp` for the list a deployment
  can set and finding nothing from this ADR in it.
