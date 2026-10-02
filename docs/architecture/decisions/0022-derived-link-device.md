# ADR-0022 — An edge's link device is derived from the edge, and the edge names its staleness limit

**Status:** Accepted
**Date:** 2026-10-02
**Complements:** ADR-0019 (§3 the Gateway stops polling a device an edge reads; §5 the link carries
the staleness limit; Consequences — this was left to implementation), ADR-0016 (a pushing tag goes
Bad on silence rather than holding a cached value), ADR-0011 (the audit trail), ADR-0002 (Core
names no protocol).

*Accepted 2026-10-02, having been put as a proposal first. The two questions it raised were
answered: the link device is derived (decision 1), and the staleness limit lives on the edge
(decision 3). One thing was settled more firmly than the proposal left it — see decision 6.*

## Context

An edge holds no devices of its own: the Gateway subscribes to what the edge publishes, and the
device that carries that subscription is the edge's **link device** — a real MQTT device in the
browse tree, with a `stalenessSeconds` that is the declared limit for everything the edge carries
(ADR-0019 §5). One limit per link rather than per plant device, because one link is what is either
silent or not.

Today an operator creates that device by hand, and it is the last thing about an edge that is
typed rather than derived. ADR-0019 records that as open:

> the link device being derived from the edge rather than named, so that nothing about an edge is
> typed by hand.

What the operator types is this:

```
name:  Edge plant-b            ← the only part that is theirs
host:  broker                  ← constant
port:  8884                    ← constant
tls:   true                    ← constant
topic: scada/edge/plant-b/samples   ← the edge's own name, in a topic
caFile:   /app/mqtt/ca.crt          ← constant, and where the Gateway mounts it
certFile: /app/mqtt/scada-gateway.crt  ← constant
keyFile:  /app/mqtt/scada-gateway.key  ← constant
stalenessSeconds: 60           ← theirs, and the only setting with a reason to vary
```

Every constant above is already a fact of the deployment rather than a choice: `deploy/cloud/docker-compose.yml`
sets `EdgeProvisioning__Host: broker`, `__Port: 8884` and `__UsesTls: "true"`, mounts the Gateway's
own three certificate files at those three paths, and `EdgeProvisioningOptions` already carries
`TopicPrefix`. **The topic is the one field that depends on the edge — and it is the edge's own
name, which the Gateway holds.**

So the hand-typed device is one name wrapped in eight fields that cannot vary. Writing it by hand
is not configuration; it is transcription, and transcription is what ADR-0019 §7 removed for tag
ids ("the two hand-typed lists become one"). This is the same list, one field short of gone.

**And it has a cost that was measured, not imagined.** Standing up the two-host walk, the link
device was created by hand for `plant-b` and the first attempt at assigning a device to that edge
was refused:

```
400 {"error":"The edge 'plant-b' has no link device yet, so this device cannot be
     assigned to it. ..."}
```

That refusal is correct — ADR-0019 refuses an assignment whose link could not carry the tags — but
it is a refusal an operator meets for getting the *order* wrong, on a form whose content was never
theirs to choose.

**One thing this must not break.** The broker keys a persistent session by the client id, and the
MQTT driver derives that id from the **device id** (`scada-darbox-{device.Id:N}`,
`MqttPushingDriverFactory`). A link device that is replaced rather than reused therefore arrives
under a new client id, and the queue the broker holds for the old one is not delivered to it. That
makes rollout an operational question with a cost, not a rename.

## Decision

**1. The Gateway derives an edge's link device from the edge, and an operator does not configure
it.**

The device exists and is ordinary — it appears in the browse tree, it is polled by the pushing
driver like any other, and its tags are watched by its own staleness limit. What changes is who
writes it: the Gateway, from the edge's name and the settings the deployment already holds.

**2. The link stays one device per edge.**

Deriving it would make a single shared MQTT device possible, and that is deliberately not done. One
subscription carrying every edge would give one staleness limit to every edge, so one silent plant
would mark every other plant's tags Bad — the failure ADR-0019 §5 chooses one link per edge to
avoid. The derivation removes the typing, not the per-link limit.

**3. The edge names its staleness limit, because the edge is the link.**

`stalenessSeconds` moves from a field an operator fills in on a device to a property of the edge.
It defaults to 60 seconds, today's value, so an edge that says nothing behaves as it does now.

This is the one setting the derivation could not simply take from the deployment: it is a judgement
about the plant's publishing rhythm, and different plants have different ones. It belongs on the
edge for the reason §5 gives it to the link — the limit is about whether *the link* is silent.

*Amended while implementing (2026-10-02).* The same argument applies to `sessionExpiryHours` — how
long the broker queues for a Gateway that is away is a property of the link too — so it moves
beside it, keeping its default of 720 hours. Two settings, and neither is typed into a device.

**4. Rollout reuses the link device an edge already has.**

The derivation is applied where no link device is configured, and an existing one is kept rather
than replaced, because replacing it changes the device id and therefore the broker's session key.
An operator who already has a link device loses nothing; an edge that has none gains one. The
migration is therefore additive, and no walk is owed for a queue that is dropped by a rename.

**5. Nothing else about a link device changes.**

An assignment is still refused unless the edge has a link device to carry it — but the operator no
longer has to make one first, so the state they could reach that refusal from is gone. A link
device is still not deleted, nor an edge's link cleared or moved, while devices are assigned.
Deleting an edge still deletes its link.

**6. A derived link device is not overridable, and this is a decision rather than a deferral.**

The proposal left this open as "not yet, and it needs a case that wants it". Having made the rest
of the decision, the answer is no, and the reason is the one the whole ADR rests on: a link device
whose topic names something the edge does not publish to is an edge that is silent — Bad tags, no
error, and nothing that says why. That is the failure the derivation exists to make
unrepresentable. An override would put it back, and put it back behind a form, where the operator
who most needs the protection is the one least able to recognise that they are removing it.

So the settings are written by the Gateway and are not editable, which is also what makes decision
3's two settings the *only* thing about a link an operator chooses: they say how long silence may
last, and nothing about where the link points.

**7. What is stored for the link is the settings that are not constants, and only those.**

`stalenessSeconds` and `sessionExpiryHours` are edge columns. The host, port, TLS flag and the
three certificate paths are read from the deployment's own `EdgeProvisioningOptions` when the link
device is built, and are not stored per edge: they cannot vary, so a column for them would be a
column an operator could make wrong.

## Consequences

- **Nothing about an edge is typed by hand.** The last hand-written thing is the edge's own name —
  which is the name in its certificate, so it is not typed twice, it is the identity.
- **A mistyped topic becomes impossible**, and with it the failure it produced: an edge that
  publishes under its certificate's name while the Gateway subscribes to a topic that spells it
  differently is an edge that is simply silent, with Bad tags and no error anywhere.
- **The refusal an operator met for getting the order wrong cannot be reached** by not having made
  a link device, because there is no longer a moment when one is missing.
- **An edge's form gains a setting**, and the storage gains a column for it. The device the link
  becomes is still a device: it still appears in the tree, and a reader who edits it is editing
  configuration the Gateway wrote.
- **One subscription per edge remains**, which is what a plant with many edges pays for the
  per-link limit. This is unchanged from today; it is recorded because deriving the device is the
  moment someone would be tempted to collapse them.
- **A deployment that upgrades keeps its sessions**, because its link devices are reused. A
  deployment that had none — or an edge added after the upgrade — gets a device whose client id is
  derived from a freshly generated device id, and the broker therefore has no queue for it. That is
  correct rather than lossy: the edge's own buffer holds anything not yet acknowledged (ADR-0017),
  so nothing is lost, and there is nothing queued under a name nothing has ever used.
- **Not decided here.** Whether the Gateway should subscribe to an edge's topics without a device
  at all. The link is a device because ADR-0019 §5 gave the limit to a driver, and a driver is
  reached through a device (ADR-0016). A future ADR could remove the device; nothing in this
  decision depends on it.

## Verified in review by

- An edge with no link device is given one by the Gateway, whose topic is the edge's own name and
  whose settings are the deployment's — and a device can be assigned to that edge without an
  operator making anything first.
- An edge that already has a link device keeps the same device, and therefore the same MQTT client
  id, across the upgrade: the broker's session for it is not dropped.
- The link device's staleness limit is the edge's setting, and a change to that setting changes
  when a silent edge's tags go Bad. Removing the per-edge limit — one shared device for every edge
  — makes one silent edge mark another's tags Bad, and fails a named test.
- Two edges have two link devices, two client ids, and two limits: silence from one leaves the
  other's tags alone.
- A link device is not deleted, nor an edge's link cleared or moved, while devices are assigned to
  it, and deleting an edge deletes its link.
- No Core type mentions MQTT, a topic or a broker (ADR-0002, ADR-0016): the derivation and the
  setting are reached through the same device and driver abstractions as before.
- A derived link device's topic, host, port and certificate paths are **not** editable, and are
  rebuilt from the edge and the deployment's own settings. Making any of them settable puts back
  the silent edge this decision removes, and fails a named test.
- An edge created on a Gateway that is already running gets its link device without a restart, and
  an edge never ends up with two.
