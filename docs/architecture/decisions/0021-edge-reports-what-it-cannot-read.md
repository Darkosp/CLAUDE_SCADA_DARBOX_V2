# ADR-0021 — An edge says which assigned devices it cannot read, on its own declaration

**Status:** Accepted
**Date:** 2026-10-02
**Complements:** ADR-0019 (§8: an edge declares its drivers and the cloud refuses what it cannot
read; §4: the configuration payload), ADR-0017 (the link carries what an edge reports; the edge's
clock is neither trusted nor overwritten), ADR-0011 (the audit trail), ADR-0016 (silence is not a
value).

## Context

ADR-0019 §8 answers one direction of a question: **the cloud must not send an edge a device it
cannot read.** The edge declares the driver keys its own build has, retained on
`{TopicPrefix}/{Edge:Name}/drivers`; the cloud records them and refuses, by name, a device whose
`driverKey` the declaration omits. An edge that has not declared yet is accepted, and the refusal
names the edge and the keys it does have.

The other direction is open, and ADR-0019 records it as open: *"A device whose `driverKey` the
edge loses after declaring it … the refusal travelling back from the edge — a payload version, and
an ADR before it."*

Here is the state it leaves. An operator assigns a `modbus-tcp` device to an edge. The edge
declares `modbus-tcp` and reads it; everything agrees. Then the edge is redeployed with a build
that has no Modbus — a stripped image, a plant that no longer speaks it, a rollback to an older
one. The edge connects, declares `["opc-ua"]`, receives its configuration, and finds a device it
cannot open. Its own log says so:

```text
Device {Device} needs driver '{Driver}', which this edge agent does not have.
```

and that is the end of it. The cloud read a declaration that no longer contains `modbus-tcp`, and
§8's rule covers the case where a **save** is what assigns the device — but this device was
assigned months ago. Nothing re-examines the assignment. So the cloud holds a device it believes
is being read, the edge reads nothing from it, and its tags go Bad by the link's staleness rule
(ADR-0016) with **nothing anywhere saying why**. An operator sees tags that stopped and a device
that looks configured.

That is the shape of defect §8 exists to prevent — loud at the plant, silent in the cloud — and
§8 closed only the half where the cloud is the one speaking.

**A second, smaller fact argues for the same answer.** The edge already knows this at startup: it
iterates its accepted configuration and checks each device's driver against its own factories
(`AcquisitionService`). What it lacks is anywhere to say it.

**And the version question is not free.** This is the first change to a message format in this
project. `EdgeDriversPayload` is at version 1 and its own remarks say a message of another version
is refused whole — *"half a declared driver list would be a list the edge never stated."* The
cloud is upgraded before the edges (`deploy/cloud/README.md` says so), which is exactly the order
that makes an edge speaking a newer version than the cloud a real state rather than a hypothetical
one. So the reader's behaviour across versions has to be decided here, not discovered later.

## Decision

**1. The edge's driver declaration carries the devices it has been assigned and cannot read.**

The message grows one field. It stays one message on one topic, because what a build can read and
what that build currently cannot open are the same fact at the same moment: both are answered by
the edge, both are retained, and both change when the edge's build or configuration changes. A
second topic would be a second thing to keep in agreement.

```json
{ "version": 2,
  "drivers": ["opc-ua"],
  "unreadable": [ { "device": "Pump Station PLC", "driver": "modbus-tcp" } ] }
```

A device is named by its **name**, not its id. The cloud owns the id and the edge was given it, so
the id would be unambiguous — but the operator reading the log, the audit row and the screen is
reading names, and the cloud resolves the name to the device it assigned. A name that no longer
resolves is still reported rather than discarded: it is what the edge believes it was told.

**2. The cloud accepts version 1 and version 2, and treats them differently on purpose.**

Version 1 is a declaration from an edge that does not say what it cannot read, which is not the
same statement as "it can read everything". So a version 1 message records the drivers and reports
**nothing** about unreadable devices — the field is absent, and absent is not empty. This is §8's
own distinction, applied one level down: an edge that has not declared is not an edge that has
declared it has nothing.

**3. The cloud records it, exposes it, and changes nothing.**

An arrival whose unreadable set is not empty is written to the audit trail with each device named
and the driver it needed, and is reported through `/api/edges` beside `declaredDriverKeys`, so
"assigned and not being read" is a state an operator can see rather than infer. **The assignment
is not changed**, for §8's reason: what an edge reads must not change as a side effect of the edge
answering, and only the edge can refuse to read it.

**4. The edge republishes its declaration when the set of unreadable devices changes.**

The declaration is published at connect, as it is now. Acquisition restarts whenever a new
configuration is accepted (ADR-0019 §6), and that restart is also when the unreadable set is
recomputed — so the declaration is republished if and only if that set differs from the one last
sent. An edge whose missing driver is restored says so by declaring a set that no longer contains
the device, without any separate "I can read it again" message.

**5. Nothing here gives the cloud a route to the device.**

The Gateway does not resume polling a device an edge is assigned (ADR-0019 §3), and does not reach
past the link to test one. The link is outbound only (ADR-0017). An unreadable device stays
unreadable until a human changes the assignment or the edge's build, and the decision is to say so
rather than to paper over it.

## Consequences

- **The two directions of §8 are symmetric again.** The cloud refuses a device the edge cannot
  read; the edge reports a device it cannot read. Neither side is the only one that can notice.
- **Tags that go Bad now have a reason attached.** A device whose driver an edge lost produces
  Bad-quality tags by the staleness rule either way; what changes is that the reason is recorded
  where the assignment is, and named, rather than being a silence an operator has to investigate.
- **This is the project's first payload version bump, and it sets the pattern.** A reader accepts
  the versions it knows and refuses the rest; a version that adds a field is accepted by the older
  reader only if the older reader is written to accept it. Here the cloud is the only reader of
  this payload, the cloud is upgraded first, and it is written to read both — so an edge that
  upgrades later is not refused. **An edge running version 2 against a cloud that predates this ADR
  is refused whole**, which is the correct and already-documented behaviour for a newer edge
  against an older cloud (`deploy/cloud/README.md`), and it is the reason the cloud half ships
  before any edge does.
- **`unreadable` is a report, not a command.** Nothing in it changes what the cloud sends or what
  an edge is assigned. If it did, an edge could rewrite the plant's configuration by failing to
  read it — which is exactly the mistake ADR-0019 §8 refuses to make for a driver list.
- **An edge with no configuration yet reports nothing unreadable**, because it has been assigned
  nothing to fail at. That is the same empty-until-configured state as `readers 0 device(s)`, and
  it must not be read as "everything is fine".
- **Not decided here.** Whether the edge should also report a device it can open but whose reads
  are failing — that is a measurement about a device rather than a fact about a build, it belongs
  with the samples' own quality, and it would want its own ADR. And whether an unreadable device
  should be shown to a Viewer as well as an Admin: `/api/edges` is Admin-only (ADR-0011),
  so today the fact reaches the people who configure plants rather than everyone who watches one.

## Verified in review by

- An edge assigned a device whose driver its build lacks declares version 2 with that device in
  `unreadable`, and the cloud records the declaration, names the device and its driver in the
  audit trail, and reports it through `/api/edges`.
- A version 1 declaration is accepted and reports **no** unreadable devices — absence is not
  emptiness — and a version 3 declaration is refused whole, naming the version.
- A version 2 declaration whose `unreadable` array is present and empty declares that the edge can
  read everything it has been assigned, and is distinguishable from the version 1 case above.
- The assignment is unchanged by an unreadable report: the device is still assigned to that edge
  after it, and the Gateway still does not poll it.
- An edge whose missing driver is restored republishes a declaration whose `unreadable` set no
  longer contains the device, with no second message kind involved.
- Removing the report — so the edge declares only its drivers, as version 1 did while claiming
  version 2 — fails a named test, and removing the version 1 acceptance fails another.
