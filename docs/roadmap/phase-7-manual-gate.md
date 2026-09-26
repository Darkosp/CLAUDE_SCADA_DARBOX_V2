# Walking the Phase 7 gate by hand

**Not yet walked.** Nothing below has been run. This is the procedure, written
from the code, the Compose files and the two deployment guides, so that the walk
has something to follow and something to be compared against afterwards. The
result slot at the end is empty on purpose. When the walk happens, what was
measured replaces this paragraph — the way `phase-5.5-manual-gate.md` and
`phase-6-manual-gate.md` record walks that had been.

**Read again against the code on 2026-09-26, and corrected where it disagreed
with it** — the `git archive` note, the edge's missing `--env-file`, what the
Gateway's log can show before a device exists, where the buffer's bound is
really set, and the two names step 6 has to note. Still nothing below has been
run.

Phase 7's test gate is in [the phase plan](phase-plan.md):

> **Test gate:** with the edge agent disconnected from the network for a
> period and then reconnected, the history contains the samples from the
> outage with their original timestamps, and nothing is invented for the
> period the link was down.

The tests and the scripted checks in steps 3 and 4 of the phase plan cover the
parts a script can see: the buffer's account balances, a batch is stored once
however often it arrives, a lost window is reported once, an edge's clock
disagreement is journalled. This is the half that needs two hosts and a link that
is really down: **an outage with a beginning and an end that somebody wrote
down**.

Installing either side is in [`deploy/cloud/README.md`](../../deploy/cloud/README.md);
backups and going back after a failed upgrade are in
[`deploy/README.md`](../../deploy/README.md). Nothing below repeats those guides.
Everything below is what to watch, what to record, and what the numbers have to
say when the walk is over.

## 0. Two decisions, before anything is started

**Which machine hosts the edge.** This one, or the plant's own hardware. A
Raspberry Pi makes the walk truer to the deployment — the edge is what runs at a
plant — but it needs an image built for `linux-arm64`, which step 3 of the phase
left unverified. If the answer is a Pi, **build and run that image first**; a walk
on hardware whose image has never run would be measuring the wrong thing.

On one machine the edge reaches the broker the way any other host does — through
the cloud stack's published 8883, not through a shared Docker network — so
`SCADA_BROKER_HOST` has to resolve **inside the edge container** to an address the
cloud server answers on, and that name must be one the broker's certificate
carries (step 3). Docker Desktop resolves `host.docker.internal` for exactly this;
on Linux the edge stack needs an `extra_hosts` entry for it. Write down what was
done: this is a trap to check before starting, not a measured fact.

**What the outage is.** Cutting the edge's own network is the literal reading of
the gate, and it leaves the cloud side untouched:

```bash
docker network disconnect <network> <edge-container>
```

Stopping the broker is the same outage seen from the other end — the edge cannot
publish, the Gateway and the database stay up — and it is one command with no
container names to look up:

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env stop broker
```

Either is repeatable. On real hardware the third answer is the true one: pull
the uplink, or close the port. Whichever is chosen, write it down, because
"I disconnected the edge" and "I stopped the broker" are not the same outage and
the log lines differ.

## 1. Before you start

- Docker on both machines. The image build takes `git archive` of the commit,
  so uncommitted edits are simply not in the images — `deploy/build-images.sh`
  says so on stderr and builds anyway. Commit first: the walk runs the commit
  the images are named for, and a fix left uncommitted cannot be in them.
- The port check from the Phase 6 walk still applies, and it was not a formality:
  something else listening on the Gateway's port is invisible to Docker and
  answers a browser first. Use `http://127.0.0.1:<port>` and never `localhost`.
- **A device that keeps producing readings**, with a tag whose Gateway id is
  copied into the edge's configuration (`edge.json`). The cloud stack's demo
  Modbus device has no simulator to read — the cloud guide says so — so on a
  laptop this means the simulators from the on-premises stack
  (`deploy/docker-compose.yml`), reached by an edge that can see them, or real
  equipment. What fed the walk is part of the record.
- **The clocks.** Write down `date -u` on the edge's host and on the cloud's at
  the start and the end. An edge whose clock is off by minutes will journal a
  `SourceClockSkew` entry, and that entry is the feature working; what matters
  here is that the history's own timestamps are the edge's, not the Gateway's.
- The edge's own clock is also what dates every sample. If it is wrong, the
  history is wrong in exactly the way this gate is checking for, so set it.

## 2. Build the images, from a commit

```bash
deploy/build-images.sh
```

In Git Bash, `export MSYS_NO_PATHCONV=1` first: without it Git Bash rewrites a
path meant for inside a container and the command fails with "No such file".
The last line names the tag — the short commit hash — and both `.env` files need
it. Keep it for the record.

## 3. Certificates

```bash
export CERT_DIR=$HOME/scada-certs
deploy/cloud/certs.sh ca
deploy/cloud/certs.sh broker mqtt.example.com broker
deploy/cloud/certs.sh client scada-gateway
deploy/cloud/certs.sh client plant-7
deploy/cloud/certs.sh expiry
```

`plant-7` is this walk's edge id; the name in the certificate is the identity the
broker checks, so it and `SCADA_EDGE_ID` must be the same string. `expiry` prints
every date: record the earliest, because a certificate that expires mid-walk
produces an outage the gate did not ask for, and its log lines look like a
configuration mistake.

## 4. The cloud side, up

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env up -d --wait
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env ps -a
```

**What must be true:** `migrator` is **Exited (0)**; `timescaledb`, `broker` and
`gateway` are running. The broker's log ends with `mosquitto version 2.1.2
running` after opening both listeners, and the Gateway's log has no `Error:`.
Nothing is subscribed to anything yet: the device that subscribes is added in the
next step, and its subscription line is checked there.

Sign in at `http://127.0.0.1:8080` (or the port in `deploy/cloud/.env`) — over the
IPv4 address, for the reason in step 1.

## 5. The device and its tag, in the Gateway

Follow the cloud guide's *Adding an edge*, step 2: a device with the driver
`mqtt`, `host` `broker`, `port` `8884`, the topic `scada/edge/plant-7/samples`,
`tls` true and the three certificate paths. Then its tag, reading from whatever
the edge is actually reading — for the simulators, the Modbus address or the OPC
UA node the on-premises walk used.

**Write down, here:** the tag's **id** (not its name — the edge names readings by
id, which is what makes the walk's SQL possible), its **scan interval**, and how
many readings a second it should produce.

The Gateway's log then shows `Subscribed to scada/edge/plant-7/samples on
broker:8884 (new session)`. On every later restart it says `(session resumed)`;
that session is what makes the broker hold the queue, so a walk that sees `new
session` twice has lost nothing only by luck.

## 6. The edge, up and reading

```bash
docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env up -d
docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env logs -f edge
```

**What must be true:** one `Connected to device <name>.` per device, and
`Connected to the broker at <host>:<port>; sending on scada/edge/plant-7/samples.`
— not "then": acquisition and the uplink are two hosted services starting
together, so the order of those two lines is not fixed. Within seconds the tag in
the web client shows a value whose time is the edge's own — a time in the past by
the link's latency, not the moment the Gateway stored it. That gap is the whole
point of the phase, and step 11 measures the grown-up version of it.

Note the container's name and its network too — `docker compose -f
deploy/edge/docker-compose.yml --env-file deploy/edge/.env ps` prints the
container, `scada-darbox-edge-edge-1` unless something named it otherwise, and the
edge stack's own network, `scada-darbox-edge_default`: the stack declares none, so
Compose makes one for it. Steps 8 and 10 need both names.

## 7. Before cutting: the baseline

Everything after this point is compared against a number that has to exist
beforehand. Read it now, while the link is up and quiet:

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env exec timescaledb \
  psql -U scada -d scada -c "SELECT count(*) AS rows, max(source_time) AS newest_measured, max(ingested_at) AS newest_stored FROM tag_sample WHERE pushed AND tag_id = '<tag-id>';"
```

**Write down all three.** `rows` is the number the outage must not disturb
outside its own window, and `newest_measured` is roughly the moment you are
about to cut at. Let a minute or two of readings go in first: a tag whose history
is empty makes every comparison below vacuous.

## 8. Cut the link

Either form from step 0. Then confirm the cut from the side you did **not**
touch, because a cut you believe in and a cut that happened are two different
things:

- If the edge was disconnected, the edge's own log is where it shows:
  `Cannot reach the broker at <host>:<port> (<reason>); <N> sample(s) wait in the
  buffer.`, repeating, with `N` growing by the scan rate. **Write down the first
  line's timestamp and `N`.** The container name comes from step 6.
- If the broker was stopped, the broker's own log goes quiet and the Gateway's
  MQTT device stays subscribed — it is not the Gateway's link. The edge says the
  same thing it would say if its cable were pulled, because from the edge's side
  those are the same event.

**Write down the cut time**, in UTC, from the clock of the machine the edge runs
on: that is the clock every sample's `source_time` comes from.

## 9. While it is down

Nothing here needs the terminal. The point of the wait is that the edge keeps
reading and keeps writing to disk — so, while it is down, look once at the web
client: **the tag shows Bad after the device's staleness limit** (60 seconds
unless the device says otherwise). Nothing is arriving, so nothing is fresh.
That is ADR-0016's rule doing its job, and it is not the defect this gate is
about; the buffer is where the readings are.

Two things worth doing while waiting, because they are the parts a short outage
cannot show:

- **Restart the edge inside the outage.** Its buffer is a file on the
  `edge-buffer` volume, not memory: `docker compose -f
  deploy/edge/docker-compose.yml --env-file deploy/edge/.env restart edge`, then
  read the first warning line again after it comes back. **`N` continues from
  where it was** — record both numbers. An edge that started counting from zero
  here has lost the readings it was holding.
- **If the lost-window path is to be exercised, it had to be decided before
  step 7.** The default `Edge:Buffer:MaxPendingSamples` is **1,000,000**, which
  at one reading a second is about eleven and a half days of outage. It is set in
  the edge's own configuration file — the copy of `edge.example.json` that
  `SCADA_EDGE_CONFIG` names, which Compose mounts as `appsettings.Production.json`
  — and **not** in `deploy/edge/.env`: that file only feeds Compose's
  substitutions, and the edge stack's `environment:` block forwards no such
  setting, so a line added there would change nothing. Put `"MaxPendingSamples":
  60` in that file (one minute at 1 Hz) and restart the edge, which re-reads it:
  `docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env
  restart edge`. The bound is not applied at startup but on the next append, so a
  bound lowered while the buffer already holds more than it drops the oldest
  readings then and there, recording a lost window of its own; do it before step
  7, with the buffer nearly empty, and the loss the walk stages is the one it
  meant to stage. See step 13.

## 10. Reconnect, and watch it drain

Reverse what was done in step 8 — `docker network connect <network>
<edge-container>`, or `docker compose -f deploy/cloud/docker-compose.yml
--env-file deploy/cloud/.env start broker`.

**Write down the reconnect time**, in UTC, from the edge's host.

**What must be true:** within seconds the edge logs `Connected to the broker at
<host>:<port>; sending on scada/edge/plant-7/samples.` again, and the warnings
stop. There is **no line announcing that the buffer drained** — the drain is
visible in the history instead, as the window from step 11 appearing in a burst,
all of it stored within a few seconds. That burst is the gate's "with their
original timestamps": the storage time is now, the measured time is then.

## 11. Prove it from the history

The walk's claim is one query. `<cut-at>` and `<back-at>` are the two times
written down in steps 8 and 10, in UTC:

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env exec timescaledb \
  psql -U scada -d scada -c "SELECT count(*) AS samples, min(source_time) AS first_measured, max(source_time) AS last_measured, min(ingested_at) AS first_stored, max(ingested_at) AS last_stored FROM tag_sample WHERE pushed AND tag_id = '<tag-id>' AND source_time >= '<cut-at>' AND source_time < '<back-at>';"
```

**What must be true:**

- `samples` is about the scan rate times the outage's length — neither zero nor
  more than the wait could have produced. Write both numbers down: this is the
  one place where a count is the evidence.
- `first_measured` and `last_measured` are **inside the outage**, on the edge's
  clock. A first measurement equal to the reconnect time is the failure this gate
  exists to catch: readings re-dated to when the link came back would look
  perfect on screen and be wrong for ever.
- `first_stored` and `last_stored` are **after the reconnect** — all of it was
  waiting on disk.

Then the two checks that say nothing was invented, and nothing was stored twice:

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env exec timescaledb \
  psql -U scada -d scada -c "SELECT count(*) AS rows, count(DISTINCT (tag_id, source_time)) AS distinct_times, (SELECT count(*) FROM tag_sample WHERE pushed AND tag_id = '<tag-id>' AND source_time < '<cut-at>') AS before_the_cut FROM tag_sample WHERE pushed AND tag_id = '<tag-id>';"
```

- `rows` equals `distinct_times`: the link is at-least-once, and the unique index
  is what makes a re-delivery a no-op.
- `before_the_cut` equals the `rows` written down in step 7. That is "nothing is
  invented for the period the link was down" in its strong form: the outage added
  rows inside its own window and touched no reading from before it.

**Worth recording, not a defect:** whether `ingested_at` ever goes backwards as
`source_time` rises (`lag` over `source_time`). A batch leaves the edge oldest
first, but a broker re-delivering a batch whose acknowledgement the edge never
saw can legitimately land after a newer one. Write it down with the log lines of
the minute it happened in; a repeating pattern, rather than one inversion, is
the finding.

## 12. The other half: an outage nothing was measured in

The gate has two clauses, and step 11 only proves the first. For the second,
with the link **up** and the cloud side untouched, stop the edge itself:

```bash
docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env stop edge
```

Wait a few minutes — long enough to be unmistakable — and write down both times,
UTC. Then start it again and wait for it to catch up.

```bash
docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env start edge
```

Run step 11's first query again with this window's times.

**What must be true:** `samples` is **0**. No reading exists, or may exist, for a
period in which nothing was reading — nothing is interpolated, and a trend that
drew a line across those minutes would be inventing a plant. The edge's own
readings from before and after are there, each with its own time; the gap is real
and visible, and that is what "nothing is invented" means. (An edge that was down
is not the same as the lost-window path in step 13: a stopped edge measures
nothing, so there is nothing to lose.)

## 13. If the buffer filled: the lost window

Only reachable with the bound lowered deliberately, per step 9. The rule is in
`deploy/cloud/README.md`: when the buffer is full, the **oldest** readings are
dropped first, and the drop is reported rather than silent.

Leave the link down long enough to overflow the bound, then, after reconnecting,
open the web client → **Journal** → **Refresh**, with the Site of the device
selected.

**What must be true:** one row naming the device, of the kind a `SamplesLost`
source event produces, saying **how many** readings were dropped and **from when
to when** — the two ends are required by the schema, so a row that names a count
without a span is a defect, not a display choice. It is one row, not one per
attempt: the edge reports the loss until a message carrying it is acknowledged,
and the loss's own id is what makes a re-report the same entry.

**If the bound was left at its default, write that down instead** — "the loss
path was not exercised; 1,000,000 pending samples at this scan rate is about
eleven and a half days" is an honest result. A gate summary that does not say
whether this ran is the kind of report that gets a phase closed on half a walk.

## What to record

Every number below is one the walk produced. None of it is in the repository, and
the note that closes the phase is only as good as this list.

**The setup:** the commit the images were built from; each host's OS and
architecture; the edge's id; the device, the tag id and the scan interval;
`MaxPendingSamples` and whether it was lowered; the earliest certificate expiry.

**The link outage, in UTC:** when it was cut, when it came back, and which form it
took (network disconnected, broker stopped, cable pulled); the edge's first
`Cannot reach the broker` line with its `N`; the buffer's `N` before and after the
edge restart, if there was one.

**What the history said:** step 7's three baseline numbers; step 11's five numbers
for the outage window; `rows`, `distinct_times` and `before_the_cut`; step 12's
window, which has to be `0`; whether `ingested_at` ever went backwards; whether a
`SamplesLost` row appeared, and what it said.

**Everything that looked wrong or merely confusing** — including anything in this
document that did not match what you saw.

## The result

*Empty. The walk has not been run.* When it is, what was measured replaces this
section, and the status note in [the phase plan](phase-plan.md) says the gate was
walked — with the numbers, not with a verdict.

## What to report

Anything that looks wrong or merely confusing, even where the behaviour is
technically correct. Phase 5.5's hand walk found ten defects a green suite had
passed, and Phase 6's found four; a walk that reports nothing unusual is worth a
second look before it is believed.

