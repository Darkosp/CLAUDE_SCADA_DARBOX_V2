# ADR-0017 — The edge-to-cloud link: our own payload over MQTT, buffered at the edge

**Status:** Accepted
**Complements:** ADR-0003 (typed value, source timestamp, quality), ADR-0006
(technology table — amended here by naming a broker), ADR-0011 (Site scoping
and the audit trail), ADR-0013 (an outage reads as an outage) and ADR-0016
(a driver may push).
**Date:** 2026-09-24

## Context

The cloud topology from the Phase 0 architecture has an edge agent in each
plant and one Gateway in the cloud. Between them is a link that will fail —
that is the whole reason the topology is interesting, and the reason it cannot
be packaged without deciding what happens while it is down.

Four things had to be settled before any of it could be written: what the edge
does during an outage, what the messages look like, which broker carries them,
and what happens when a long outage fills the buffer.

## Decision

**1. The edge acquires and buffers; it does not evaluate alarms.**

The edge agent reads its devices on their scan intervals, historises nothing
locally beyond its send buffer, and ships samples to the cloud. Alarms are
evaluated in the cloud Gateway, as they are today.

The consequence is stated plainly because it is the cost of this choice:
**while the link is down, nobody is watching.** ADR-0013 already makes that
visible rather than silent — evaluation stop and start are journalled, and the
period reads as an outage rather than as a quiet stretch. A plant whose
process genuinely needs alarms during an internet outage runs the on-premises
topology (Phase 6), where nothing depends on a link. Putting an alarm engine
in the edge as well would mean two alarm journals that have to be merged on
reconnection, with two engines disagreeing about what happened while they were
apart; that is a larger decision and, if it is ever needed, its own ADR.

**2. Our own payload, not Sparkplug B.**

A message carries exactly what ADR-0003 defines: tag id (the stable id from
ADR-0001, never the display path), the typed value, the source timestamp the
edge recorded, and the quality. Sparkplug B is the industry standard for MQTT
in this space and would let third-party tools read the same stream, but its
metric model would have to be translated to and from ours on every message,
and a translation layer between two models is where timestamp and quality
semantics go to die quietly. If interoperability with a third-party system is
ever required, a Sparkplug adapter is a module beside this one, not a
replacement for it.

**3. Mosquitto, with TLS and a certificate per edge.**

This amends ADR-0006's table, which named MQTTnet (a library) but no broker.
Mosquitto is small, open, and enough for one broker serving many edges; a
clustered broker can replace it later without touching the payload.

- The link is TLS, and each edge authenticates with **its own client
  certificate**. A shared password across sites would make every site as
  exposed as the least careful one.
- An edge may publish **only under its own topic prefix**, enforced by broker
  ACL as well as by the Gateway. Site scoping (ADR-0011) is not weakened by
  the fact that data now arrives over a broker: an edge that publishes another
  site's topic is refused at the broker and the attempt is audited.
- Delivery is QoS 1, over **MQTT 5**, and **the edge removes a batch from its
  buffer only when the broker has acknowledged it**. At-least-once means
  duplicates are possible; the Gateway makes ingestion idempotent per (tag,
  source timestamp) rather than trusting that they will not happen.

  *Added while implementing (2026-09-25):* the protocol version is not a
  detail. This ADR first said only "QoS 1". In MQTT 3.1.1 a `PUBACK` carries
  no reason code, so a publish the broker **refused** arrives looking exactly
  like one it accepted — and the edge would then delete from its buffer the
  batch that was thrown away, silently, which is the one thing the buffer
  exists to prevent. MQTT 5 carries the reason code. A mutation proved it:
  under 3.1.1 a refused batch left 0 pending and 20 "acknowledged".

  *Added while implementing (2026-09-25):* "acknowledged" has to mean
  **stored**, and two things were quietly making it mean "received". The
  Gateway subscribed without a persistent session, so a batch published while
  it was down — a restart, an upgrade, a broker restart — was acknowledged to
  the edge by the broker and delivered to nobody; and the Gateway acknowledged
  a batch even when writing it had failed, for instance with the database
  unreachable. Either one turns a buffered outage into a silent hole, which
  defeats the buffer entirely. So: **the Gateway connects with a persistent
  MQTT 5 session under a fixed client id and `clean start = false`, and it
  acknowledges a batch only after that batch is durably stored.** The broker
  holds messages while the Gateway is away; the edge keeps its copy until the
  cloud has really got it. Mosquitto's queue limits have to be set with that in
  mind — the default silently discards beyond a modest depth, which would
  reintroduce the same hole one level down.

**4. The buffer is bounded, drops oldest first, and says so.**

The buffer is on disk and survives an edge restart — an outage that includes a
power cut is an ordinary outage. It has a configured bound. When a long outage
fills it, **the oldest samples are dropped**, because an operator reconnecting
needs to know what the plant is doing now more than what it was doing nine
hours ago.

A drop is **never silent**. The edge records what window it lost, and the
Gateway journals that on reconnection, so the history shows a stated hole
rather than an unexplained one. This is the same rule as ADR-0013's: a gap
somebody can see beats a gap somebody has to infer.

**5. The edge's clock is not trusted, and not overwritten either.**

Samples carry the edge's source timestamp (ADR-0003). The Gateway also records
when it received them, and never substitutes one for the other. If an edge's
timestamps are implausible against the Gateway's clock beyond a configured
skew, the samples are still stored — inventing better times is not an option —
but the skew is journalled, so the cause is visible when a trend looks wrong.

## Consequences

- An outage costs alarm coverage, not data: the measurements come back with
  their original times, and the journal shows both the outage and anything the
  buffer could not hold.
- One more thing to operate: a broker, its certificates, and their expiry.
  Certificate expiry is a scheduled outage that arrives without warning if
  nobody is watching for it, and the deployment guide has to say so.
- The payload is ours, so the format is a compatibility surface we now own
  across versions. It needs a version field from the first message, before
  there is any second version to need it.
- Ingestion is idempotent per (tag, source timestamp), which also protects
  against a replayed buffer after a crash.

## Verified in review by

- With the edge disconnected for a period and then reconnected, the history
  contains the samples from the outage **with the timestamps the edge
  recorded**, and nothing is invented for the period the link was down. This
  is the Phase 7 gate.
- There is a test in which the buffer is filled past its bound: the oldest
  samples are dropped, the lost window is recorded, and it appears in the
  journal after reconnection. Removing the record makes it fail — a silent
  drop must not pass.
- There is a test that the same batch delivered twice produces one set of
  samples, not two.
- There is a test that an edge publishing another site's topic is refused, and
  that the refusal is audited.
- The edge buffer survives a restart of the edge process with its contents
  intact.
- No Core type mentions MQTT, Mosquitto or a topic (ADR-0002, ADR-0016).
