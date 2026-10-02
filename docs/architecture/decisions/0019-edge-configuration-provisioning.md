# ADR-0019 — How an edge is configured: the cloud is the source of truth, delivered over the link it already has

**Status:** Accepted
**Complements:** ADR-0001 (the tag's stable id), ADR-0002 (the core/module
boundary), ADR-0003 (a value that has no honest reading is not invented),
ADR-0011 (the audit trail), ADR-0016 (a driver declares itself polled or
pushing), ADR-0017 (the edge-to-cloud link).
**Date:** 2026-09-27

## Context

Phase 7 built the link: an edge reads a plant, buffers what it read, and
ships it to the cloud Gateway over MQTT with a certificate per edge
(ADR-0017). What Phase 7 did not decide is where the edge's list of devices
and tags comes from. At the time this ADR was written it was a hand-written
`edge.json`, mounted into the edge container read-only, and it had to name the
**cloud** Gateway's tag ids — `src/EdgeAgent/EdgeOptions.cs` said so
explicitly, and called out that how the list reaches an edge was still open.
That is the question this ADR answers.

*Amended 2026-10-01.* The sentences that stood here said the file was still
written by hand because the cloud derived no configuration yet, and that the only
part of the decision below that was built was the assignment (2026-09-27). Both
halves are built now: the assignment (`fe829f0`, this repository's PR #6), and the
delivery — `EdgeConfigurationBuilder`, `EdgeConfigurationPublisher`,
`EdgeConfigurationPayload`, the edge's own `EdgeConfigurationSource` and
`EdgeConfigurationConsumer`, the cloud Compose wiring and the broker ACL. What has
not happened is the walk. Nothing about the decisions below changed; a statement
that stopped being true was corrected, as `README.md` in this directory requires.

*Amended 2026-10-02.* The walk recorded in
[`phase-7-manual-gate.md`](../../roadmap/phase-7-manual-gate.md) found the case
this ADR left to implementation: a device whose `driverKey` no edge driver
answered to was accepted by the Gateway, derived into the configuration,
published, and refused only by the edge — loud on the plant machine, silent in
the cloud. §8 decides it. Nothing else about the decision changed.

That leaves two lists a human must keep in agreement:

- the cloud Gateway's MQTT device and its tags, created in the web client;
- the edge's `edge.json`, listing the plant's devices and, for each tag, its
  address and the cloud tag's id.

The only thing joining them is the tag id, typed by hand on both sides. The
costs of getting this wrong are quiet and real. A mistyped id attaches a
plant's readings to the wrong tag, or to none, and the mistake surfaces as a
wrong number on a screen rather than as an error. Adding a device in a plant
needs a person to edit a file on a machine at that plant and paste ids out of
a browser. Nothing knows which edge owns which device, so nothing can be
reconciled, audited or automated, and the two lists drifting apart is
invisible.

The forces that shape any answer:

- **The link is outbound only.** No inbound port is opened at the plant —
  ADR-0017's own constraint, and the reason the topology survives an OT
  security review. So a configuration must be either fetched by the edge or
  delivered over the connection the edge already holds.
- **An edge already has an identity.** Its certificate's name is its id, and
  the broker's ACL lets it publish under that name only (ADR-0017).
- **The cloud is where configuration already lives.** Devices, tags, folders
  and their addresses are configured in the Gateway and scoped by Site
  (ADR-0001, ADR-0011). The edge is the only place a second, independent copy
  exists.
- **The cloud's view of an edge is flatter than the plant.** The Gateway
  models an edge as a single MQTT device holding every tag the edge reads,
  while the edge reads one or more plant devices, each with its own driver,
  scan interval and address space. The plant's structure exists only at the
  edge today.

## Decision

**1. The cloud is the source of truth for what an edge reads.**

A device, its tags, and each tag's plant address and kind are configured in
the cloud Gateway. The edge holds no independently authored list; the file it
is started with is derived, not typed.

**2. An `Edge` exists as an entity, and a device is assigned to at most one
edge.**

The edge's identity is the name in its certificate (ADR-0017). The assignment
— which edge reads which device — is what makes "this edge's configuration" a
question with an answer. A device with no assignment is acquired by the
Gateway itself, exactly as today, so the on-premises topology is unchanged.

**3. A device assigned to an edge is acquired by that edge, not by the
Gateway.**

Its driver key, scan interval, settings, and its tags' addresses travel to the
edge, which reads them; the Gateway does not poll it and receives its values
over the link instead. The same driver module runs on both sides (ADR-0002),
so a device's driver names *how it is read where it is reachable* — the same
meaning in both topologies. This keeps a plant's real shape in the cloud's
browse tree instead of collapsing every edge into one flat MQTT device.

**4. The Gateway derives each edge's configuration and publishes it over the
link that already exists.**

A per-edge topic under the edge's own prefix, retained and versioned; the edge
subscribes to it over the same outbound TLS connection it already holds. No
inbound port is opened at the plant (ADR-0017 holds), and the broker's ACL
already confines an edge to its own prefix.

**5. The configuration is versioned, and the edge keeps the last one it
accepted.**

The edge applies the newest version it has seen and keeps the last accepted
one across a restart. While the link is down it keeps reading the last
configuration it had — store-and-forward applies to configuration as it does
to samples. A change made while an edge is away is delivered when it returns,
because the topic is retained.

**6. A new configuration is applied by restarting acquisition.**

The edge reads its configuration once at start-up (`IOptions<EdgeOptions>`,
and `AcquisitionService` iterates it). A newer version is applied by restarting
the acquisition service. Live reload is a later refinement and is not part of
this decision.

*(Settled while implementing, 2026-09-28.)* Not out of `IOptions<EdgeOptions>`:
the edge's own options hold its id, its broker and its buffer, and
`AcquisitionService` iterates the configuration the edge last accepted, kept in
the buffer beside its samples. The message is `EdgeConfigurationPayload`
(Drivers.Mqtt, version 1) and the topic is `{TopicPrefix}/{Edge:Id}/config` —
the name in the edge's certificate, which the broker's ACL confines it to, so
an edge can read no other edge's configuration and no other edge can read its.

**7. The tag id is never typed by hand.**

The derived configuration carries each tag's own stable id (ADR-0001). The two
hand-typed lists become one.

**8. An edge declares which drivers it has, and the cloud refuses, by name, a
device that edge cannot read.**

Which driver keys exist is a fact about the build that runs the device, and the
cloud's build is not the edge's. The Gateway registers Modbus, OPC UA and MQTT;
an edge registers Modbus and OPC UA. So the cloud cannot answer the question
from its own list — it holds `mqtt`, which no edge can read — and a build that
gave an edge a driver the cloud lacks would be the same mistake mirrored. The
edge is therefore the one that says what it has, over the connection it already
holds, retained on its own topic under its own prefix.

A device is refused when it is assigned to an edge that has declared its drivers
and does not have the device's. The refusal names the edge and the keys it does
have, so an operator is told what to do rather than that something went wrong.
This is the refusal migration 0013's link guard already makes, for the same
reason: a device an edge cannot read is a state in which nothing reads its tags
and nothing says so.

An edge that has **not** declared yet is not a wrong one. It may never have
started, and a plant's devices are ordinarily configured before its edge is, so
an assignment to an undeclared edge is accepted — the cloud cannot know yet —
and the edge is shown as having declared nothing, so that "nobody has told us"
is never read as "we checked and it is fine". When a declaration arrives that
omits the driver of a device already assigned to that edge, the cloud records
the declaration in the audit trail with the device named (ADR-0011) and leaves
the assignment alone: what an edge reads must not change as a side effect of the
edge answering, and only the edge can refuse to read it.

*(Settled while implementing, 2026-10-02.)* The message is `EdgeDriversPayload`
(Drivers.Mqtt, version 1) on `{TopicPrefix}/{Edge:Name}/drivers`, retained and
republished after every connect — a fact about a build, not a measurement, so it
does not go stale and is not given an expiry. The broker's ACL gives each edge
write on its own declaration and the Gateway read on all of them.

## Consequences

- The operator configures a plant once, in the cloud. No file is edited on a
  machine in a plant, and no id is copied out of a browser.
- A plant's devices appear in the cloud's browse tree in their real shape —
  one node per device — instead of every edge flattened into a single MQTT
  device. The per-edge MQTT device an operator creates today becomes a
  property of the assignment rather than something anyone configures.
- The Gateway must distinguish the devices it polls from the ones it only
  receives. That distinction already exists as a property of the driver
  (ADR-0016); this ADR gives it a second source — the edge assignment —
  rather than inventing a new one.
- A tag's plant address becomes cloud configuration, so changing it is an
  audited configuration change (ADR-0011) rather than a file edit nobody sees.
- The link now carries configuration as well as samples. A configuration is
  small and changes rarely, so it rides a retained topic without competing
  with the sample stream.
- Removing a device or a tag from an edge's assignment stops the edge reading
  it. Samples already in the edge's buffer are still delivered — the buffer is
  not rewritten — and the tag receives nothing new, so its last value goes Bad
  by the staleness rule (ADR-0016) rather than freezing silently.
- The configuration format is a compatibility surface the project now owns,
  exactly as the sample payload is (ADR-0017), and needs a version field from
  the first message.
- **Writing to a tag whose device is assigned to an edge is not decided here,
  and is refused until it is.** The link is outbound only, so the Gateway has
  no route to that device: the write path opens its own connection to the
  device (`TagWriter`) and would wait out its ten-second deadline before
  reporting a device error that never happened. A write to such a tag is
  therefore refused with a named reason and audited, rather than attempted.
  Routing a write to the edge — over the link, with the result reported back —
  is a decision of its own and needs an ADR before any code.
- **A tag whose device is assigned to an edge is watched by the link that
  carries it, and no new setting is invented for that.** *Added while
  implementing (2026-09-27).* Once the Gateway stops polling such a device,
  nothing feeds its tags, so they must go Bad by the staleness rule (ADR-0016)
  rather than freeze at their last value (ADR-0003). But ADR-0016 gives the
  limit to a **driver**, and the Gateway has no driver for a device an edge
  reads: it never opens a connection to it. The limit therefore belongs to the
  transport that does carry the values — the edge's **link**, which in the
  Gateway is a pushing device whose `stalenessSeconds` is already the declared
  limit for everything it carries. So an edge names its link device, that
  device carries the tags of every device assigned to the edge, and its limit
  covers them. One limit per link rather than per plant device, because one
  link is what is either silent or not. `PushedSources:ClockSkewTolerance` is
  not reused for this: it is about a clock, not about silence.
  That makes the two halves one change. Excluding a device from polling before
  its link carries its tags would leave those tags with no source at all, and
  the MQTT driver **refuses** a sample naming a tag the device does not carry,
  so the link has to know the assignment before it can deliver anything. For
  the same reason an assignment is refused, by name, unless the edge has a
  link device; and a link device is not deleted, nor an edge's link cleared or
  moved, while devices are assigned. There is deliberately no state in which a
  device is read by an edge while the Gateway neither polls it nor watches a
  link for it.
- **An edge's driver keys are declared by the edge, and are not configuration
  the cloud edits.** *Added 2026-10-02 with §8.* They are a property of the
  build at the plant, so there is no edge form for them and no default: an edge
  that has not declared is shown as having declared nothing, not as having the
  cloud's list. The declaration is a compatibility surface like the two payloads
  beside it, and carries a version from its first message.
- **Not decided here, and left to implementation:** live reload instead of a
  restart; how a device moving from one edge to another is ordered so that no
  reading is attributed twice; and the link device being derived from the edge
  rather than named, so that nothing about an edge is typed by hand.

## Verified in review by

- A device and its tags are configured in the cloud and assigned to an edge;
  the edge reads them with **no hand-written file on the plant machine**, and
  its readings reach the history under the cloud's own tag ids.
- A device with no edge assignment is still acquired by the Gateway itself —
  the on-premises path is unchanged.
- With an edge offline, a configuration change is made in the cloud; on
  reconnect the edge applies it, and until then it kept reading the
  configuration it had.
- The edge keeps its last accepted configuration across a restart taken while
  the link is down.
- There is no path in which a human types a tag id into an edge's
  configuration.
- A write to a tag whose device is assigned to an edge is refused with a named
  reason and audited, and no connection is opened to a device the Gateway
  cannot reach (ADR-0003: a refusal must not report a failure that did not
  happen).
- With a device assigned to an edge that names a link device, the Gateway
  starts **no scan loop** for that device, and the link device is started with
  the device's tags among those it carries. Removing the assignment filter
  makes the scan loop start again; removing the link's wider tag list makes the
  device's samples be refused as tags the link does not have.
- An assignment is refused, by name, unless the edge has a link device; and a
  link device is not deleted, nor an edge's link cleared or moved, while
  devices are assigned to it. There is no state in which a device is read by an
  edge while the Gateway neither polls it nor watches a link for it.
- A device whose driver the edge has declared it does not have is **refused at
  the save that assigns it**, and the message names the edge and the drivers it
  does have. Changing an assigned device's driver key to one the edge does not
  have is refused the same way.
- A device assigned to an edge that has declared nothing yet is accepted, and
  the edge reports that nothing has been declared — never the cloud's own list.
  When that edge's declaration arrives without the assigned device's driver, the
  audit trail names the edge and the device, and the assignment is left as it
  was.
- An edge's declaration reaches the cloud over the link: republished after a
  broker restart, read by the Gateway, and refused at the broker for any edge
  that publishes under another edge's name.
- No Core type mentions MQTT, Mosquitto or a topic (ADR-0002, ADR-0016,
  ADR-0017).
