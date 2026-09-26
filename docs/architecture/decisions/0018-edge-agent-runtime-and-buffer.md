# ADR-0018 — The edge agent runs on the CLR, and buffers in SQLite

**Status:** Accepted
**Amends:** ADR-0006's technology table on two points — the edge agent is no
longer Native AOT, and Microsoft.Data.Sqlite joins the table for the edge
buffer. The rest of ADR-0006 stands.
**Complements:** ADR-0017 (the edge buffers on disk, bounded, dropping oldest
and recording what it lost).
**Date:** 2026-09-24

## Context

Two questions had to be answered before the edge agent could be written, and
both were answered by building and running rather than by reading.

**Native AOT does not work with OPC UA.** The real OPC UA module from this
repository was published Native AOT for linux-x64 and run: it fails with
`MissingMethodException: No parameterless constructor defined for type
'Opc.Ua.Bindings.TcpTransportChannelFactory'` — before it reaches the network.
The same code under the ordinary runtime gets as far as `Connection refused`,
which is the correct answer when nothing is listening. The OPC UA stack builds
objects by reflection, and AOT removes what reflection needs; the build also
warns about DataContractSerialization, so this is not one missing type but a
class of them. Modbus and our own MQTT module publish and run fine under AOT.

That leaves a choice nobody would make on purpose if it were stated plainly:
keep AOT and have no OPC UA in a plant, or keep OPC UA and give up AOT. AOT
bought a smaller binary and a faster start. OPC UA is the second protocol most
likely to be waiting in an actual plant — it is why Phase 4 exists.

The third option, forcing the types to survive AOT, was not attempted and is
deliberately not chosen: a list of types kept alive by hand is a list that goes
stale on the next library update, and it goes stale in a customer's plant
rather than here.

**The buffer's hard part is not writing, it is dropping.** ADR-0017 requires
that when a long outage fills the buffer, the oldest samples are dropped *and*
the lost window is recorded *and* the cursor moves. Those three either all
happen or none do, including across a power cut. With SQLite that is one
transaction. Written by hand over segment files it is a protocol we would have
to design and prove ourselves — length-prefixed records with checksums, a
recognised torn tail, an fsync order, an atomic cursor update — and the part
that matters most, fsync ordering under a real power cut, is the part no test
can demonstrate.

SQLite under Native AOT was also verified working (2.8 MB, no trim warnings),
so the buffer choice does not depend on the runtime choice; they are recorded
together because they amend the same table.

## Decision

**The edge agent is published self-contained for its target, on the ordinary
runtime — not Native AOT, and not trimmed.** Trimming removes what reflection
needs for the same reason AOT does.

**The edge buffer is SQLite, through Microsoft.Data.Sqlite**, with WAL and
`synchronous=FULL`, and the drop-oldest step is a single transaction covering
the delete, the recorded lost window and the cursor.

Both entries join ADR-0006's table. Mosquitto is already there from ADR-0017.

## Consequences

- The edge binary grows from a few megabytes to tens, and starts more slowly.
  On edge hardware that runs a plant this is not the constraint; having every
  protocol the plant speaks is.
- OPC UA works at the edge exactly as it does in the Gateway, with no second
  code path and no "supported except at the edge" footnote in the manual.
- The buffer's durability is SQLite's problem rather than ours. We can test a
  killed process; we cannot test a real power cut, and we now do not have to
  claim we have.
- One more native dependency to carry per platform (`e_sqlite3`).
  **linux-arm64 was open here and is not any more.** On 2026-09-27 both
  `e_sqlite3` and the agent were published for arm64, the image was built for it,
  and the agent was run under emulation with its readings reaching the cloud
  database ([the record](../../roadmap/phase-7-manual-gate.md#since-the-walk-linux-arm64)).
  A board is still unverified: if edge hardware is a Raspberry Pi or similar, run
  the image on it before the hardware is chosen — the image exists now, which is
  what this entry said was owed.
- If a future edge target genuinely cannot run the CLR, this decision is what
  gets revisited — with a new ADR, and with the OPC UA question answered first.

## Verified in review by

- The edge agent's publish profile is self-contained, with AOT and trimming
  off, and a comment naming this ADR so nobody turns them back on for tidiness.
- The OPC UA module loads and connects from the edge agent, proved by running
  it, not by it compiling.
- There is a test in which the buffer is filled past its bound: after the drop,
  the lost window is present and the cursor is consistent with what remains.
  Killing the process mid-write and reopening leaves the buffer readable, with
  no half-written batch delivered as if it were whole.
- The buffer survives a restart of the edge process with its contents intact
  (ADR-0017).
