# ADR-0023 — A tag write is routed to the edge that reads the device, and is never queued

**Status:** Accepted
**Date:** 2026-10-03
**Complements:** ADR-0019 (§3 the Gateway stops polling a device an edge reads — this is the
Consequences entry it left open), ADR-0017 (the link: outbound only, our own payload, TLS per
edge, and the buffer that makes store-and-forward work), ADR-0016 (a driver declares what it can
do), ADR-0011 (the audit trail), ADR-0003 (a value that has no honest reading is not invented —
here, a result that was never reported is not success), ADR-0002 (Core names no protocol).

*Accepted 2026-10-03, having been put as a proposal first, and accepted with one thing added
rather than settled: decision 4's bound is five seconds, and decision 8 is new — a deployment may
turn writing over the link off entirely. That switch is the answer to the concern this ADR raises
about itself: a plant that must not be commanded from the cloud can say so, and the default is
today's behaviour rather than a new one.*

## Context

A tag can be written: `TagWriter` opens a connection to the device and the driver's
`WriteAsync` sets the value. That works for a device the Gateway polls, and it cannot work for one
an edge reads, because the Gateway never opens a connection to that device (ADR-0019 §3). The link
is outbound only — no inbound port is opened at the plant — so "the Gateway writes it" is not a
route that exists.

Today the write is **refused by name and audited**:

```text
409  {"error":"This tag is read by edge 'plant-b', which the Gateway cannot reach."}
audit: tag.write_refused   {siteId, deviceId, edgeId}
```

That refusal is honest and it is the right thing to do while nothing else is decided: the
alternative would be to attempt a connection that cannot be made and report a device error that
never happened, which is the class of untruth ADR-0003 exists to prevent. ADR-0019 records the
rest as deliberately open:

> Routing a write to the edge — over the link, with the result reported back — is a decision of its
> own and needs an ADR before any code.

This is that decision. Two things make it harder than the sample path it mirrors.

**First, the direction is reversed.** Samples, driver declarations and configurations all flow one
way, and the edge is always the one that starts. A write is the cloud wanting something to happen
at a plant that it cannot dial. The only route is the connection the edge already holds — so the
cloud publishes, and the edge subscribes, exactly as it already does for its configuration.

**Second, and this is the part that is easy to get wrong: a write is not a sample.** A sample that
arrives late is still a true reading of the moment it was taken, which is why ADR-0017 can buffer
one for hours. A write that arrives late is a **command to change a plant after the reason for it
has passed**. The value of a write is in the present tense. Keep that in view, and most of the
decisions below follow from it:

- a write must not be retained on its topic, because a retained write is one an edge receives the
  moment it reconnects, having missed the moment;
- a write must not be queued behind an outage the way a sample is, for the same reason;
- a write that was not acknowledged must not be reported as done.

## Decision

**1. The cloud publishes a write request on the link the edge already holds, and the edge executes
it.**

`{TopicPrefix}/{Edge:Name}/writes`, published by the Gateway and subscribed to by that one edge —
the same connection, the same certificate, the same mutual TLS as the configuration the other way.
The edge resolves the tag to the device it is already reading, calls the same driver module the
Gateway would have called (ADR-0002), and answers.

No inbound port is opened at the plant. ADR-0017 holds unchanged.

**2. One write is one message with one id, and the reply names it.**

```json
{ "version": 1, "writeId": "…", "tagId": "…", "value": { "kind": "numeric", "numeric": 4.5 } }
```

The reply, on `{TopicPrefix}/{Edge:Name}/write-results`, carries the same `writeId` and either the
value the driver acknowledged or the reason it refused:

```json
{ "version": 1, "writeId": "…", "tagId": "…", "outcome": "written" }
{ "version": 1, "writeId": "…", "tagId": "…", "outcome": "failed", "reason": "…" }
```

The id is what makes "the result reported back" checkable rather than hopeful: a reply that does not
name the request in flight is not the answer to it, and is ignored. Each operator action gets a
fresh id, so a reply can never be mistaken for the answer to a later write to the same tag.

**3. The write topic is never retained, and the write is never buffered.**

Published with QoS 1 so it is not lost to a lost packet, and **without the retain flag**, so an edge
that connects a minute later does not receive a command from a minute ago. The edge does not put a
write in its disk buffer: the buffer exists to carry measurements across an outage (ADR-0017), and
there is no honest way to carry a command across one.

**4. The API waits for an answer within five seconds, and says it did not get one.**

A write succeeds, or it is reported as failed with the edge's reason, or the deadline passes and the
caller is told exactly that. It is never answered before the edge has answered, and it is never
answered as though it had.

Five seconds: one MQTT round trip over one link, with room for a device that answers slowly, and
short enough that an operator who has asked a plant to do something is not left guessing. **504**,
not 202 — a write is a request for something to have happened, so "accepted" would be the untrue
answer this ADR exists to avoid.

A write whose edge is silent therefore ends in a timeout that names the edge, and the read path's
own staleness rule is what says whether that edge was there at all (ADR-0016) — the write path does
not invent a second notion of liveness.

**5. Both ends record what happened, and they are different records.**

The Gateway audits `tag.write` with the outcome and the edge that produced it — the operator's
record, and the one that answers "who changed this and did it work". The edge logs what it attempted
and what its driver said — the plant's record. Neither is a substitute for the other: the Gateway
cannot see the device, and the edge does not know who asked.

**6. A refusal keeps its current shape, and the write's route does not change what may be written.**

`IsWritable` and the Operator role are checked before anything is published, so the request that
reaches an edge is one that was already permitted. The refusal for a read-only tag and the refusal
for a missing permission are unchanged.

**7. Nothing about the read path changes.**

This adds a topic in the cloud-to-edge direction and a reply topic in the other. Samples, driver
declarations and configurations are untouched, and `tag.write_refused` disappears only because
there is nothing left to refuse — an assigned device is now reachable, through the edge that reads
it.

**8. A deployment may turn writing over the link off, and the default is that it is on.**

`EdgeProvisioning:WritesEnabled`, off meaning a write to an edge-assigned device is refused exactly
as it is today — by name, and audited. It is the one control this ADR adds beyond the three
guardrails above, and it exists because for some plants commanding equipment from a cloud is not a
capability that should be reachable by configuration drift: it is a decision the deployment makes,
and this is how it makes it.

Defaulting to on is deliberate rather than incidental. It is what today's behaviour is for a device
the Gateway polls — an Operator may write one, and always could — so a deployment that upgrades
gains nothing it did not already have; what it gains is the same ability extended to the devices it
put behind an edge. Off is available to anyone who does not want that, and the refusal it produces
names the setting rather than leaving an operator to wonder why the write is rejected.

## Consequences

- **A plant device behind an outbound-only link becomes writable**, which is what the refusal was
  always a placeholder for.
- **The link now has a payload in each direction that is not a measurement**, and it is the first
  one the cloud can use to ask for something. That is the shape of a control path, and it is worth
  naming plainly: this is the ADR after which the cloud can change a plant. It is bounded by the
  three decisions above — not retained, not buffered, not reported as done until it is — and by
  nothing else, so anyone tempted to relax one of them should read decision 3's reason first.
- **A write is slower than it was for a polled device**, by one MQTT round trip over the plant's
  link. That is the cost of the device not being dialable, and it is why the deadline is seconds
  rather than the write path's old ten.
- **A write can fail where a polled device's could not**: the edge may be there and the device may
  not. The reply carries the driver's own reason, so "the edge could not reach the device" and "the
  edge does not have that driver" are told apart rather than collapsed (ADR-0003).
- **An edge that is offline makes every write to its devices fail after the deadline**, where
  previously it failed immediately with a named refusal. That is slower and it is more honest: the
  old refusal was true only while no route existed, and once one does, "I asked and got no answer"
  is the fact.
- **A device an edge reads is still not writable *by the Gateway*.** There is no path in which the
  Gateway opens a connection to it; ADR-0019 §3 stands.
- **Not decided here.** What a write means for a device whose driver has no `WriteAsync` at all —
  the MQTT link's own pushing driver is one, and it is the device the link *is* rather than a device
  an edge reads, so it is not this path. And whether a write should be refused outright when the
  edge is known to be silent, rather than waiting out the deadline: that would need a liveness
  signal the write path could trust, and the read path's staleness rule is deliberately the only one
  (ADR-0016).

## Verified in review by

- A write to a tag whose device is assigned to an edge reaches that edge over the link, and the
  device the edge reads changes — proved with a device an edge actually reads, not a stand-in.
- A write is **not** delivered to an edge that was offline when it was published and connects
  afterwards: the topic is not retained and nothing queues it.
- A write the edge cannot perform is reported as failed with the edge's reason, and the API's
  answer names it rather than reporting success.
- A write whose edge does not answer ends at the deadline with an answer that says so, and no audit
  row claims it was written.
- A reply carrying a `writeId` that is not the request in flight is ignored: it can neither complete
  a write nor fail one.
- The permission and `IsWritable` checks still happen **before** anything is published, so an
  unpermitted or read-only write never reaches an edge.
- With writing over the link off, a write to an edge-assigned device is refused by name, audited,
  and **nothing is published** — the setting is a refusal and not a filter applied after the fact.
- No Core type mentions MQTT, a topic or a broker (ADR-0002), and the edge writes through the same
  `IDeviceDriver.WriteAsync` the Gateway uses.
