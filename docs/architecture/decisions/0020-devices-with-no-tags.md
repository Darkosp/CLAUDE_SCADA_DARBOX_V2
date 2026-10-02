# ADR-0020 — A device with no tags is not derived into an edge's configuration

**Status:** Accepted
**Date:** 2026-10-02
**Complements:** ADR-0019 (the cloud derives each edge's configuration; §4 the
payload, §8 the cloud refuses what an edge cannot read), ADR-0003 (a value that
has no honest reading is not invented), ADR-0011 (the audit trail).

## Context

An edge's configuration is derived from the running catalogue (ADR-0019 §4):
`EdgeConfigurationBuilder.DevicesFor` takes the devices assigned to an edge and
gives each one its driver key, scan interval, settings and **its tags**. A device
that has just been created has no tags, and is assignable to an edge in that
state — the assignment is an ordinary edit of the device, and nothing in the API
requires a device to have tags before one exists.

The payload reader refuses a device whose tag array is empty
(`EdgeConfigurationPayload`, "device '{name}' has no tags") and refuses the
**whole message** with it, because acceptance is whole-or-nothing: a configuration
is not partly applied. That refusal is right. An edge reads tags; a device with no
tags is a thing with nothing to read, and a reader that accepted it would be
carrying a device that can never produce a value.

The two together are the defect. The cloud derived a device the edge would refuse,
published it, and the edge rejected the entire configuration — including every
**other** device that edge was meant to read. This is the shape ADR-0019 §8 exists
to prevent, on the other axis: §8 closed "the cloud publishes a device whose
*driver* the edge cannot read"; this is "the cloud publishes a device with nothing
*to* read". §8's answer was to refuse the assignment at the save, by name. That
answer does not transfer here, and the reason is the order an operator works in:
tags are added to a device that already exists (`POST /api/devices/{id}/tags`), so
refusing the assignment until tags exist would make the natural sequence
— create the device, assign it, add its tags — impossible, and force a second save.
It would trade a silent misconfiguration for a guaranteed extra step on every
device at every plant.

The walk recorded in
[`phase-7-manual-gate.md`](../../roadmap/phase-7-manual-gate.md) found this by
doing it: a device assigned before its tags existed travelled in the message and
the edge refused all of it. Nothing was lost — the edge kept reading the last
configuration it accepted — but the cloud published something it had no reason to
believe was readable.

## Decision

**The builder omits a device that has no tags, and the payload reader keeps
refusing one that arrives with none.**

The device stays assigned to the edge; it is simply not something the edge is told
to read yet. The moment its first tag is saved, the catalogue changes, the derived
content changes, and the configuration is republished — so the device reaches the
edge without an operator doing anything a second time. The reader's refusal is not
weakened: it is the contract that made the omission necessary, and it is what stops
any future producer from sending an unreadable device.

**The omission is named in the Gateway's log, once, with the device and the edge.**

Silence is what made this a finding rather than a decision. An operator who assigns
a device and sees nothing happen has been told nothing; a line naming the device
and why it is absent is the difference between a system that is waiting and a
system that is broken. It is not an error: an assigned device with no tags is a
legitimate state that an operator is in the middle of leaving.

## Consequences

- **The natural order works.** Create a device, assign it to an edge, add its tags;
  the device appears on the edge when its first tag is saved, and republishing is
  already the publisher's behaviour (it compares the derived content's hash).
- **An assigned device that is not read is a state, not a fault.** The cloud shows
  it assigned and the edge does not read it, and the log line is what says which of
  those two facts is the reason. The alternative — a device the edge refuses along
  with everything else — is strictly worse, because it takes the readable devices
  down with it.
- **A device with no tags assigned to an edge whose devices are all tagless derives
  an empty configuration**, which is a configuration in its own right: that edge
  reads nothing (ADR-0019 §4). This is the same message an edge with nothing
  assigned receives, and the edge's treatment of it does not change.
- **The edge no longer has to be defended against its own cloud.** §8 gives the
  cloud the edge's driver list so it can refuse an unreadable driver; this gives the
  derivation one rule it can apply alone, from the catalogue, with no second party.
  Both remain: a device with no tags is omitted, and a device whose driver the edge
  lacks is refused at the save.
- **Not decided here.** Whether the web client should show a tagless assigned device
  differently from a read one — the API already reports the assignment, and the
  question is presentation. And whether a device that *loses* all its tags is
  treated the same way: the derivation would omit it, its tags would go Bad by the
  link's staleness rule (ADR-0016), and nothing has yet walked that path.
  It appears in `docs/roadmap/open-work.md` under what the next walk owes.

## Verified in review by

- A device with no tags, assigned to an edge, is **absent** from that edge's derived
  configuration, and every other device assigned to the same edge is present.
- The same edge's configuration is published successfully and accepted whole by an
  edge — the regression the finding describes cannot recur: no configuration the
  builder produces is one the reader refuses.
- Saving a device's first tag causes the configuration to be republished with that
  device present, with no second edit of the device.
- The payload reader still refuses a device whose tag array is empty. Removing that
  refusal, or removing the omission, each fails a named test.
- The omission is logged once with the device's name and the edge's, and not once
  per publish.
- An edge with every assigned device omitted derives an empty configuration and
  reads nothing, rather than failing.
