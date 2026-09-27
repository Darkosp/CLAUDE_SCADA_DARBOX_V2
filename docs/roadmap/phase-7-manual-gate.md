# Walking the Phase 7 gate by hand

**Walked on 2026-09-26, on one machine**, with the link cut for real: the edge
was disconnected from the cloud stack's own Docker network for 2 min 5 s, its
buffer filled to a deliberately lowered bound of 60 readings, the history holds
readings measured inside the outage with the edge's own timestamps, an outage in
which nothing was measured added no row at all, and the readings the bound forced
out are one journal entry naming how many and from when to when.

The procedure below is what was followed. Where the walk found it wrong, the step
says so and [The result](#the-result) names it in the list of what the walk found;
the numbers are there too. The step headings, the commands and the claims are
otherwise as they were written.

**Read again against the code on 2026-09-26, and corrected where it disagreed
with it** — the `git archive` note, the edge's missing `--env-file`, what the
Gateway's log can show before a device exists, where the buffer's bound is
really set, and the two names step 6 has to note.

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
plant — and it needs an image built for `linux-arm64`, which step 3 of the phase
left unverified; that image exists now, and has run under emulation and only under
emulation ([Since the walk](#since-the-walk-linux-arm64)). If the answer is a Pi,
**build and run that image on the Pi first**; a walk on hardware whose image has
never run there would be measuring the wrong thing.

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

**How it is called matters, on the commit that was walked.** As committed then,
`certs.sh` has no executable bit, so on Linux or in WSL — where there is no Git
Bash to run a file that lacks `+x` — the line above is `sh deploy/cloud/certs.sh
<args>`: `sh` is the interpreter the file's own first line names, and under it the
script is fine. `bash deploy/cloud/certs.sh` was not: one of the script's own
`${2:?…}` messages contained an apostrophe (`give the broker's DNS names…`), which
`bash` reads as an opening quotation mark, so it stopped with `unexpected EOF while
looking for matching '` before running a line. The walk met both, in that order.
This repository's PR #3, merged after the walk, removes the apostrophe and sets the
bit, after which either form works; see [The result](#the-result).

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
  line's timestamp and `N`.** The container name comes from step 6. Two things
  about that first line are easy to misread: the application's output carries no
  timestamp of its own, so add `-t` to `docker logs`, and the line arrives tens of
  seconds after the cut — the walk's came 25 s late and already read `49`, because
  readings keep buffering while the publisher is still finding out that its socket
  is gone. A walker who checks a few seconds after cutting, sees nothing and
  concludes the cut did not happen is reading a log that has not caught up.
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
  meant to stage. See step 13. **And it decides what steps 11 and 12 can count**:
  at 60 with two readings a second, the retained window is the last 30 s of the
  outage, so `samples` comes out near the bound rather than near the scan rate
  times the outage's length — and the outage has to run past the bound before
  anything is staged as lost at all.

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
  one place where a count is the evidence. **Unless the bound was lowered, per
  step 9** — then it is the bound that decides, and the walk's two tags came out
  at 28 and 29 rather than the ~125 a 2 min 5 s outage at 1 Hz could have
  produced.
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
- `before_the_cut` is **at least** the `rows` written down in step 7, and equal to
  it only if that baseline was taken at the instant of the cut. Saying "at least"
  weakens nothing that matters: the outage added rows inside its own window and
  changed no reading from before it. The walk's two numbers were 160 and 148 —
  twelve readings whose `source_time` is before the cut were published in the 13 s
  between the baseline and the cut, and arrived after it.

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
UTC. **Record the last reading measured before the stop and the first one after,
not the times the commands ran.** `docker compose stop` lets the container shut
down gracefully and the restart's first readings land a second after the command,
so a window taken between command times can catch readings on either side of it:
the walk's first attempt counted 16 readings that were all measured *after* the
edge came back. Then start it again and wait for it to catch up.

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
eleven and a half days for one tag at 1 Hz, and five and three-quarter days for the
two this edge's device carries" is an honest result. A gate summary that does not
say whether this ran is the kind of report that gets a phase closed on half a walk.

## Appendix: the whole walk on one machine

Step 0 allows this machine to host both ends, and the walk above did. What that
changes, so the next one does not have to work it out again — none of it is a
substitute for two hosts and a real link:

- **The edge reaches the broker over the cloud stack's own Docker network.** After
  the edge stack is up, join it —
  `docker network connect scada-darbox-cloud_default scada-darbox-edge-edge-1` —
  and set `SCADA_BROKER_HOST=broker`. That name is already in the broker's
  certificate from step 3, so nothing is reissued and no `extra_hosts` entry is
  needed. The published 8883 and `host.docker.internal` are the other route, and
  that one needs the certificate to carry its name as well.
- **The simulators are reached the same way, and started on their own**: `docker
  compose -f deploy/docker-compose.yml --env-file deploy/.env up -d modbus-sim
  opcua-sim`, then `docker network connect scada-darbox_default
  scada-darbox-edge-edge-1`. They publish nothing, so a network is the only way to
  them, and starting the whole on-premises stack instead would put a second
  Gateway on the same 8080 and a second database behind a volume of its own for
  nothing.
- **The outage is the network disconnect.** It is the literal reading of the gate,
  it leaves the cloud side untouched, and on one machine it is the only form that
  does not need the publish path reasoned about first.
- **One clock, not two.** Every timestamp in such a walk comes from one machine's
  clock, so the record should say that rather than imply that the skew half of
  step 1's check ran.
- **The C# suite asks the host about Docker.** Tests that run `docker compose
  version` or `docker version` skip when the CLI is not on the host running
  `dotnet test`: with Docker inside WSL and the suite in Windows, 17 of them skip.
- **The database the suite is pointed at is the walker's choice, and `localhost`
  is not always it.** `SCADA_TEST_DB_HOST` has to name the address the database
  answers on — here WSL's — because a PostgreSQL that already owns
  `localhost:5432` on the Windows side answers first and is not that server.

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

Walked on 2026-09-26, by one person, on one machine. Every number below is one the
walk produced; the two ends of it — the cut and the reconnect — are times that
were written down as they happened, from the clock of the host the edge ran on.

### The setup

| | |
|---|---|
| the commit | `23232df` (`23232df0ecfec33a256da24a2dc0a663e4f191cf`), `main == origin/main`, nothing uncommitted |
| the images | all five, built from that commit by `deploy/build-images.sh`, tagged `23232df` |
| the hosts | one: Windows 11 Home 10.0.26200 for the browser and the C# suite, and Ubuntu 26.04.1 in WSL2 (kernel 6.18.33.2, `linux-x64`) for Docker 29.1.3 and Compose 2.40.3, holding all three stacks |
| the edge | id `plant-7`, certificate signed by the walk's own CA, `SCADA_BROKER_HOST=broker`, joined to the cloud stack's network and to the simulators' |
| the device | `Edge plant-7` (`338cf425-8f8e-465f-af46-2a9bbc148e0f`), driver `mqtt`, Site `Bitola`, host `broker`, port `8884`, topic `scada/edge/plant-7/samples`, `tls true`, the Gateway's three certificate paths |
| the tags | `Discharge Pressure` (`ccfd90b3-f8aa-4e7f-8269-a74df739a224`, `holding:0`) and `OPC Pressure` (`f95b4476-d3b7-49d7-a749-ebe51cddd292`, `ns=2;s=Pump1.Pressure`) |
| the cadence | the edge's own `ScanIntervalMs` 1000 on each of its two devices: 1 reading a second per tag, 2 in all. The Gateway's device is pushing, so it has no scan interval |
| the bound | `MaxPendingSamples` **60**, down from 1,000,000, written into the file `SCADA_EDGE_CONFIG` names before step 7 |
| certificates | the three leaves expire **2028-12-29 19:50:33 GMT**, the earliest; the CA 2036-09-23 |
| what fed the edge | the two simulators from the on-premises stack, started on their own in `scada-darbox_default` |
| the clock | one, reading `19:57:17Z` when the baseline was taken |
| step 5 | the device and its two tags were created through the API the browser itself calls (`POST /api/sites/{id}/devices`, `POST /api/devices/{id}/tags`); the browser was not used at that step |

Step 1's port check and its clock notes were done as written, and so was step 2 —
except that the build ran from the WSL side (`bash deploy/build-images.sh`, where
the checkout is LF), so Git Bash's `MSYS_NO_PATHCONV` trap never came up. Step 3
needed the calling form it now records, and step 5 was done through the API rather
than the browser; both are in the table above.

### The link outage

The form was the edge's own network — the literal reading of the gate, with the
cloud side untouched:

```bash
docker network disconnect scada-darbox-cloud_default scada-darbox-edge-edge-1
```

| | |
|---|---|
| cut | **`2026-09-26T19:57:37Z`** |
| back | **`2026-09-26T19:59:42Z`** — 2 min 5 s later |
| the first `Cannot reach the broker` line | `19:58:02.094Z`, **25 s after the cut**, already reading `49 sample(s) wait in the buffer` |
| `N` at the bound | 60 from `19:58:06Z` onwards and never higher: the bound held and 190 readings were dropped behind it |
| the edge restarted inside the outage | stopped `19:59:07Z`, started `19:59:08Z`; its first warning after the restart read **60**, not 0 — the buffer is the file, not the process |
| the drain | `19:59:43.124Z`, 2 s after the reconnect: `Connected to the broker at broker:8883; sending on scada/edge/plant-7/samples.` |

The warnings stop at that line, and nothing announces the buffer draining: the
history is where the drain shows, as a burst.

### What the history said

Step 7's baseline, taken at `19:57:24Z`, with the readings still arriving:
`rows` **148** for each tag; `newest_measured` `19:57:24.390Z` and
`19:57:24.874Z`; `newest_stored` `19:57:24.586Z` and `19:57:25.191Z`.

Step 11, over `source_time >= 19:57:37Z AND < 19:59:42Z`:

| | Discharge Pressure | OPC Pressure |
|---|---|---|
| `samples` | 28 | 29 |
| `first_measured` | `19:59:13.848Z` | `19:59:12.983Z` |
| `last_measured` | `19:59:41.042Z` | `19:59:41.205Z` |
| `first_stored` | `19:59:43.146Z` | `19:59:43.146Z` |
| `last_stored` | `19:59:43.146Z` | `19:59:43.146Z` |
| `rows` / `distinct_times` | 209 / 209 | 211 / 211 |
| `before_the_cut` | 160 | 160 |
| `ingested_at` going backwards as `source_time` rises | 0 | 0 |

Against the gate: every retained reading was **measured inside the outage** — the
first 28 s in, the last a second before the link came back — and **stored after
it**, all of them in the same instant, which is the drain arriving as one burst.
`rows` equals `distinct_times`, so a re-delivery was a no-op although the link is
at-least-once.

**The lost window** (step 13) was one row, in `alarm_event` under
`event_type = 'SamplesLost'` — not in `audit_log`, and not one row per attempt:

| | |
|---|---|
| `lost_samples` | **190** |
| `gap_from` | `2026-09-26T19:57:37.467Z` |
| `gap_until` | `2026-09-26T19:59:12.843Z` |
| `recorded_at` | `2026-09-26T19:59:43.140Z` |
| `loss_id` | `3d4a6c41-01ad-4543-ab81-03d37a6d8603` |
| what it names | the device, Site `Bitola`, `tag_path` `Bitola/Edge plant-7` |

Count and span are both there, and the ends close on the numbers above: the loss
starts at the cut and stops where the retained readings start (`19:59:12.843` is
the OPC tag's `first_measured`, a millisecond away), and 250 readings were due in
the 125 s at 2 a second — 60 kept, 190 reported lost, which is the bound exactly.
57 of those 60 fall inside the window above; the rest were measured in the second
the link came back, which the window excludes by its own definition.

**Step 12**, the outage in which nothing was measured, with the link up: the last
reading before the edge was stopped is `20:00:14.231Z` and the first after it was
started again is `20:03:16.463Z`. Readings with `source_time` in `[20:00:45Z,
20:03:15Z)` — inside that window with minutes to spare: **0**. The `Bad` a tag
goes to when its device falls silent was checked one 80-second stop later, in the
data the browser shows: both of the edge's tags reported `quality: Bad` with
their last `sourceTimestampUtc` unchanged.

### The suite

With the database reachable — `SCADA_TEST_DB_HOST` at the address WSL answers on,
`SCADA_APP_DB_PASSWORD=scada_app` — all seven projects exit 0:

| project | passed | skipped | total |
|---|---|---|---|
| Core.Tests | 100 | 0 | 100 |
| EdgeAgent.Tests | 15 | 0 | 15 |
| Persistence.Tests | 62 | 0 | 62 |
| Drivers.Mqtt.Tests | 26 | 0 | 26 |
| Drivers.OpcUa.Tests | 13 | 0 | 13 |
| Drivers.Modbus.Tests | 27 | 0 | 27 |
| Gateway.Tests | 68 | 17 | 85 |
| **all seven** | **311** | **17** | **328** |

Of the 133 a machine with no database skips, the 116 that needed only a database
now run. The 17 that remain ask the *host* for Docker — `docker compose version`,
`docker version` — and skip without it, which is a different fact from a missing
database and was true of those 17 before and after. A run with a database reports
more tests in total because a class whose fixture skips reports each of its
theories as one skipped case rather than as its cases.

The web client's own suite is not part of this walk. It was run on this commit
earlier in the same session — `npm ci`, then `npm test`, 47 of 47 — and not again
here.

### What the walk found

Six things. None is a defect in what the gate is about, which is worth saying
plainly: what the phase claims held up.

1. **`certs.sh` cannot be run with `bash`, and is committed without an executable
   bit.** `bash deploy/cloud/certs.sh ca` stops with `unexpected EOF while looking
   for matching '` before running a line, because an apostrophe in the script's own
   `${2:?give the broker's DNS names…}` reads as a quotation mark to `bash`. `sh`,
   the interpreter its first line names, is unaffected, and so is Git Bash, which
   is how every earlier walk ran it. But the file's mode in the repository is
   `100644`, so on Linux or in WSL the documented `deploy/cloud/certs.sh ca` fails
   with `Permission denied`. Step 3 records the working form for the commit as it
   was, and this repository's PR #3, merged after the walk, removes the apostrophe
   and sets the bit.
2. **The application's log carries no timestamps.** Step 8 asks for the first
   `Cannot reach the broker` line's *timestamp*; nothing in the container's output
   has one until `docker logs -t` adds it. The step says so now.
3. **That first line is not early, and its `N` is not small.** It arrived 25 s
   after the cut, already reading 49, and `N` steps with each failed attempt
   rather than once a second. A walker who checks a few seconds after cutting,
   sees nothing and concludes the cut did not happen is reading a log that has not
   caught up.
4. **Step 11's `before_the_cut` equals step 7's `rows` only if that baseline was
   taken at the instant of the cut.** Here 160 against 148: twelve readings whose
   `source_time` is before the cut were published in the 13 s between the baseline
   and the cut, and stored after it. The step says "at least", with the reason.
5. **Step 11's `samples` is the bound's number, not the outage's, once step 9's
   bound has been lowered** — 28 and 29 rather than the ~125 the outage's length
   and the scan rate give. The two steps depend on each other and neither said so.
6. **Two smaller ones in the same family.** Step 5 asks for the tag's scan
   interval, and an edge's device has none: it is pushing, and the cadence is the
   *edge's* `ScanIntervalMs`, which is the number worth writing down. And step 12's
   "write down both times" means the times the readings stop and start, not the
   times the commands ran — the walk's first attempt at that window caught 16
   readings measured *after* the edge came back, which reads as a defect and is not
   one.

### What was not measured

- **A link between two hosts.** Everything ran on one machine: no cable, no
  uplink, no second kernel. The disconnect is the form step 0 allows, and the only
  form that cannot show what a switch, a firewall or a NAT does to a session — nor
  fail the way a real link fails.
- **Two clocks.** Both ends read one machine's clock, so no `SourceClockSkew`
  entry could appear and none did: the clock-disagreement half of step 1's check
  was not walked. It is covered by tests.
- **The browser.** Step 5's device and tags were created through the API the
  browser itself calls, and step 9's `Bad` was read from `/api/tags` rather than
  off the screen. Those are the values the screen shows; that the screen shows
  them is not something this walk checked. The screen has since been walked, on
  2026-09-27, and found one defect in the client
  ([below](#since-the-walk-what-the-screen-says)).
- **A real arm64 machine**, and so any walk on plant hardware. When this walk ran,
  `build-images.sh` built from the commit but the Dockerfile pinned `linux-x64` and
  only `linux-x64.pubxml` existed, so edge hardware was a piece of work before it
  was a walk. It has been built since, and run under emulation on this machine,
  which says the publish and the image are arm64 and says nothing about a board —
  [Since the walk](#since-the-walk-linux-arm64).
- **The web client's suite on the walk's day**, per the note above. Run again on
  2026-09-27 on `main`: 47 passed, 0 failed, 0 skipped
  ([below](#since-the-walk-what-the-screen-says)).
- **A second outage with the bound left at 1,000,000**, which is the run in which
  `samples` is the scan rate times the outage's length rather than the bound, and
  in which nothing is staged as lost. Walked as step 5 of the same procedure
  ([below](#since-the-walk-what-the-screen-says)).

### Since the walk: `linux-arm64`

**2026-09-27, on the same one machine.** The walk left this open, and why it was
open is that the Dockerfile and the publish profile together could only build x64:
`PublishProfile=linux-x64` was written into the `RUN`, and no arm64 profile existed
for a build to name.

What changed is that the image now builds for the platform it is *for*, not for the
one it is built on. `ARG TARGETARCH`, which BuildKit sets from `--platform`, picks
between two publish profiles whose names are the RIDs — so the RID cannot be
forgotten, a build on the edge machine itself needs no flag, and an architecture
with neither profile is refused rather than guessed at. `deploy/build-images.sh`
takes an arch argument: `arm64` builds the edge agent alone (the only image an edge
machine runs) and tags it `-arm64`, so an arm64 image cannot quietly replace the x64
one under the same tag. `deploy/edge/docker-compose.arm64.yml` is the run below, not
a deployment.

**Built, with numbers.** `deploy/build-images.sh HEAD arm64` on this x64 machine,
with binfmt registered so the one architecture check in the image can run emulated:

```
scada-darbox/edge-agent:e5dd394-arm64    Architecture: arm64    345 MB    21 s
```

The native build is unchanged: same command, five images, `amd64`, 319 MB — the
same 319 MB as the walk's own `23232df`.

**Run, under emulation.** `docker compose -f deploy/edge/docker-compose.yml -f
deploy/edge/docker-compose.arm64.yml --env-file deploy/edge/.env up -d`, with
`SCADA_IMAGE_TAG=<commit>-arm64`, joined to the cloud stack's network — the walk's
own form, unchanged. What it showed:

- `docker exec … uname -m` → `aarch64`: the userland in the image is arm64, and QEMU
  is what ran it.
- The agent's log: `Connected to device Pump skid.`, `Connected to device Discharge
  PLC.`, and `Connected to the broker at broker:8883; sending on
  scada/edge/plant-7/samples.`
- The broker's log: `New client connected from 172.18.0.5:42402 as plant-7 (p5, c1,
  k15, u'plant-7')`, `negotiated TLSv1.3 cipher TLS_AES_256_GCM_SHA384`, and the
  disconnect when the stack came down 87 s later.
- The cloud database, `ingested_at` inside that window: `f95b4476…` (the OPC UA tag,
  Pump skid) 79 readings from `source_time` 22:43:31.6 to 22:44:50.5, and
  `ccfd90b3…` (the Modbus tag, Discharge PLC) 89 readings from 22:43:21.3 to
  22:44:50.3 — stored 0.7 and 0.4 s after they were measured, so they carry the
  edge's clock and not the store's. Two other tags have rows in the same window
  whose `source_time` equals `ingested_at` to the microsecond: those are the cloud
  Gateway polling its own devices, and they are exactly the rows this claim has to
  exclude rather than count.

**What the work found, inside the image's own build.** The last stage compares the
architecture it is in with the profile the SDK stage published. Those two are
decided in different places — the platform and `TARGETARCH` — so they can disagree
with nothing looking wrong, and on the first attempt they did: `ARG
TARGETARCH=amd64` *with a default* wins over the `TARGETARCH` BuildKit supplies, so
a `--platform linux/arm64` build published `linux-x64`, and the image said so —
`this image is linux-arm64, but the binary in it was published for linux-x64` —
rather than shipping an `-arm64` tag on an x64 binary. Both platform arguments are
declared without defaults now, and every build goes through BuildKit, whose absence
`build-images.sh` reports in a sentence instead of leaving Docker to fail on an
empty platform.

**Still not measured: a board.** This is QEMU on x64 answering for arm64. It is the
strongest check available without a Raspberry Pi and it is not a substitute for one:
a plant run on a real aarch64 machine — and a link between two hosts — remain owed,
as the list above says.

**Merged as this repository's PR #4** on 2026-09-27, the image build and this
record together.

### Since the walk: what the screen says

**Prepared 2026-09-27, and walked the same day** — the steps below, run with a browser open
rather than a terminal, which is the one thing the walk above could not do for itself. What it
closes is the third bullet of
[What was not measured](#what-was-not-measured): the device and its tags were created
through the API the browser itself calls, and the `Bad` was read from `/api/tags`
rather than off the screen. With it goes the working rule this repository states
(`CLAUDE.md`): a phase that has a screen is not done until someone has used the
screen. It is the only one of the list's open bullets that needs no second machine.

**The client's suite, run again.** 2026-09-27, in `src/Web`: `npm test` — **47 passed,
0 failed, 0 skipped**. That is the list's fourth bullet, which was "not run on the
walk's day"; it has been run since, on `main` at the commit this section was added to,
and the client's own source is unchanged from the walk's. Run once more with the `.tag`
fix below in place: still **47 passed, 0 failed** — a missing space is not a thing these
tests can see, which is the whole reason a person found it.

**The state it starts from, and one thing to read correctly.** The walk left 17,478
pushed readings in the cloud database, across the two tags (`ccfd90b3…` 8,744,
`f95b4476…` 8,734, both from 19:54:56 on 2026-09-26 to 22:44:50), one `SamplesLost`
entry and two `EvaluationStarted` engine events, and those were the newest rows until
this procedure's own run added more. **The last of them are the arm64 run's**, after
local midnight: the local clock read 2026-09-27 while the edge dated its samples
2026-09-26, which is the two-clock question in miniature and not a defect. The edge had
been down since, so its tags were past their staleness limit — the screen would have
shown that, which is exactly what step 9 asked for and what nobody has looked at.
**Preparing this procedure brought the edge back up on 2026-09-27**, with a configuration
file that names no buffer bound, so the bound's default applies — which is step 5's run.
Its readings are live in the cloud database and on the screen from that moment, and the
numbers the walk recorded above are still there beside them.

**Checked before the screen, on 2026-09-27**, so that a check failing on the screen is
the screen's failure and not the environment's: the uplink logged `Connected to the
broker at broker:8883; sending on scada/edge/plant-7/samples.`; both devices connected
and their tags read `Good again` — after `Name or service not known` for the two
simulators, which is the appendix's own two-network note: the edge has to join the
cloud stack's network for `broker` and the on-premises one for `opcua-sim` and
`modbus-sim`, and it did not resolve the second pair until it did. 73 readings were
stored in the cloud database within a minute of the edge starting. `/api/drivers`
answers with `mqtt` as its one `"pushing": true` entry, which is what step 4 turns on.
And the two edge tags answer `Good` with numeric values and a `sourceTimestampUtc` about
a second before `ingested_at` — the edge's clock, not the store's.

**1. Sign in, over the IPv4 address.** `http://127.0.0.1:8080`, with the Admin named in
`deploy/cloud/.env` (`SCADA_INITIAL_ADMIN_USERNAME` / `SCADA_INITIAL_ADMIN_PASSWORD`).
Never `localhost`, for step 1's reason. Hard-refresh before judging anything: a browser
holding the previous client is Phase 6.5's finding.

**2. Browse: the edge's readings are on the screen, and they are not the browser's
clock.** The edge's two tags hang under `Edge plant-7`. **What must be true:** each
reading is Good and moving, and the time beside it is the edge's — in the past by the
link's latency, not the moment the page drew it. A tag that has never received
anything reads `No data since <time>` instead (ADR-0016's wording); neither form is a
defect.

**3. Journal: the lost window, in words.** The Journal view, Site-filtered. **What must
be true:** the `SamplesLost` entry names how many readings were dropped and between
which two times, in sentences and times a person reads — not column names, not raw
microseconds. The two `EvaluationStarted` entries belong to no site and must be visible
to a Site-scoped reader as well, because a Viewer on one Site still has to know the
system was not watching (ADR-0013).

**4. The device form knows a driver can push.** Browse → New device, and type `mqtt`
into **Driver**: *before anything is saved*, the scan-interval field disappears and a
line says "This driver pushes its values when they change, so there is no scan
interval." Capitals count for nothing — the client trims and lowercases the key, as the
Gateway does — and a driver key it has not been taught keeps the field, which is the
same "unknown is polled" rule. Nothing is saved by looking, so this step can be undone
by leaving the form.

**5. The live path, and the outage, watched rather than queried.** This is also the
run the list above owes: **a second outage with the bound left at 1,000,000**, in which
`samples` is the scan rate times the outage's length and nothing is staged as lost. The
edge is already up with that bound (see above); the walk's own file said 60. With values
arriving per step 2, cut the link as in step 8 and watch the client **without
refreshing**:

- values arrive and move, each carrying a time in the past by the link's latency —
  the push channel, not a poll;
- after the staleness limit (60 s by default) the reading on screen turns Bad and
  **gives up its value**: a Bad reading draws `—` where the number was, rather than a
  stale one carried forward, which is `tag.ts`'s rule and ADR-0016's. Where the
  *reason* is, this step had wrong: it promised one "where the value is", and no
  screen can show it — `TagSnapshot` carries no reason (`Core.Tags.TagSnapshot`), the
  client has nowhere to put one, and a **pushing** device logs nothing at all when it
  falls silent (`DeviceScannerService.WatchForSilenceAsync` marks the tags and says
  none). The reason that exists is the edge's own, in the edge's log at step 8
  ("Cannot reach the broker at broker:8883 (…)"), and for a polled device the
  Gateway's ("Cannot reach device X; its tags will report Bad quality."). So step 9's
  sentence is something seen as a quality, its cause something read elsewhere — the
  second finding below;
- on reconnect, the tag's last 15 minutes fill with the outage window in a burst, drawn
  at the times the edge measured rather than at the moment they arrived: the visual
  form of step 11's "with their original timestamps".

**5, the cut, watched from both ends.** The link was cut at **`2026-09-26T23:42:10Z`** with
the bound left at its default, which is the run the list above owed: no `Buffer` block is in
the configuration file this edge was started with, so `EdgeOptions.MaxPendingSamples` applies
its own `1_000_000` and nothing is staged as lost.

The baseline (step 7), taken while the link was still up, one second before the cut at
`23:42:09Z`:

| | Discharge Pressure | OPC Pressure |
|---|---|---|
| `rows` | 10,520 | 10,509 |
| `newest_measured` | `23:42:09.434775Z` | `23:42:09.494472Z` |
| `newest_stored` | `23:42:09.589054Z` | `23:42:09.589054Z` |

The edge's own side of the cut (step 8): its first warning came at **`23:42:47.232Z`**, 37 s
after the cut — later than the walk's own 25 s — and already read **74** samples waiting,
which is that delay times the 2 readings a second the two tags together produce. Every line
after it grew the count by 4: `74`, `78`, `82`, `86` at two-second intervals, and by
`23:46:44Z` it read **546**, which is 4 min 36 s of outage at that rate. Nothing is being
dropped, and the default bound is further away than the note in step 13 says: at 2 a second
it is about five and three-quarter days, not eleven and a half — that estimate was for one
tag at 1 Hz.

At the staleness limit — 60 s by default, `MqttPushingDriverFactory.DefaultStalenessLimit` —
the reading turned **Bad and gave up its value**, which is what the corrected bullet above
says a reader sees. `/api/tags` answered for both tags with `quality: Bad`, a value of kind
`none` and a `sourceTimestampUtc` frozen at that tag's last real reading
(`23:42:09.434Z` and `23:42:09.494Z`, the baseline's own numbers, unmoved), and the client
draws exactly that: `—` where the number was, the unit still beside it, and the word `Bad`
beside those. No reason, anywhere. No journal row either — the loss path in step 13 needs a
full buffer, and nothing is lost.

**On the burst, and the numbers step 11 asks for.** The edge's own line says when it came back:
`23:56:52.292834554Z`, `Connected to the broker at broker:8883; sending on
scada/edge/plant-7/samples.` The drain has no line of its own, and in the history it has almost
no duration at all: the readings measured while the link was down arrived in a **56.9 ms**
window — `23:56:52.302280Z` to `23:56:52.359215Z` — 1,754 of them, all inside one second.

| step 11, per tag | Discharge Pressure (`ccfd90b3…`) | OPC Pressure (`f95b4476…`) |
|---|---|---|
| `samples` | 877 | 877 |
| `first_measured` | `23:42:10.438073Z` | `23:42:10.501512Z` |
| `last_measured` | `23:56:51.272160Z` | `23:56:51.754426Z` |
| `first_stored` | `23:56:52.302280Z` | `23:56:52.302280Z` |
| `last_stored` | `23:56:52.359215Z` | `23:56:52.359215Z` |

Every one of the five is where the step says it must be. `samples` (877) is the scan rate times
the outage's length — 14 min 42 s at 1 Hz — and nowhere near the `1,000,000` the buffer would
have had to hold for step 13's loss path to fire; the edge's own count, `1,750` at its last
warning before the reconnect (`23:56:50.176Z`), is that arithmetic written from the other side,
and the four readings it measured in the 2.1 s between that line and the reconnect are the
difference. `first_measured` and `last_measured` are **inside the outage on the edge's clock** —
the first 0.44 s after the cut, the last between half a second and a second before the link came
back — so not one reading was re-dated to the moment it arrived, which is the failure this gate
exists to catch. `first_stored` and `last_stored` are **after the reconnect**: all of it was
waiting on disk. The oldest reading in the batch waited **14 min 41.9 s** to be stored and the
last of them 0.6 s, and no replayed row was stored before it was measured — ADR-0017's rule, the
edge's timestamp carried across untouched and the store's own recorded beside it, read off one row
rather than inferred from two.

The two checks that say nothing was invented and nothing stored twice, at `00:03Z`: **11,788**
and **11,776** rows, each equal to its own count of distinct `(tag_id, source_time)` — the link
is at-least-once and the unique index makes a re-delivery a no-op; `before_the_cut` **10,520** and
**10,509**, equal to step 7's baseline exactly, because that baseline was taken 0.4 s before the
cut and no reading measured before it arrived after it; and **zero** inversions of `ingested_at`
against `source_time` over the whole history since the cut, so the batch left the edge oldest
first and nothing landed twice out of order.

**On the burst, what the chart had to draw.** The chart asks one query, and it is the client's
own: `loadHistory()` in `app.ts` takes `from` as `to` less 15 minutes, and `history()` in
`api.ts` turns it into `GET /api/tags/{id}/history?from=&to=`. Asked at the instant of the
reconnect it answers with **895 samples per tag** — first measured `23:41:52.363Z`, last
`23:56:51.272Z`, every one of them `Good` and kind `numeric` — and the store divides them
**18 and 877**: the 18 stored while the link was still up, the newest of them measured
`23:42:09.434Z` and `23:42:09.494Z`, which are step 7's `newest_measured` to the microsecond,
and then the 877 that were measured with nothing to send them. Asked again afterwards it still
holds the outage: the minute `23:45:00Z – 23:46:00Z` answers with 60 samples per tag, measured
in that minute and all stored at `23:56:52.302Z`, quality `Good`, so the storage time is now
and the measured time is then. The biggest step between two consecutive readings in the
replayed window is 1.0 s, which makes the drawn line unbroken rather than dotted. That is step
5's third bullet — the last 15 minutes filling with the outage window at the times the edge
measured, not at the moment they arrived — and the visual form of step 11's "with their
original timestamps".

And the journal, for the second outage: `/api/alarms/journal` still answers with **three** rows,
the same three the first walk left — its `SamplesLost` (`190`, `21:57:37 – 21:59:12` local) and
its two `EvaluationStarted` — because a buffer that never filled stages nothing: the loss path
belongs to the bound, not to the link. A row that is not there is this run's evidence that
nothing was invented, in the very place a reader looks for the opposite.

**The screen is not the evidence.** Step 11's query stays the evidence and its numbers
are still the ones to write down; what this walk adds is whether a person can reach that
evidence without a terminal. Both, or the phase is closed on half a check.

**What the screen said, 2026-09-27.** The four steps that need a person were answered by
one, with the browser open on `http://127.0.0.1:8080` — the IPv4 form, never `localhost` —
and a hard refresh before anything was judged. The fifth step's cut was then run with that
same screen in front of the walker.

**1 and 2, on the screen.** Signed in as the Admin: the header shows `CONNECTED` and
`admin · Admin`, the Site picker holds `Bitola`, and the tree holds `Edge plant-7`, marked
`mqtt`, with `Discharge Pressure` and `OPC Pressure` under it. The readings were live and
moving, each timed by the edge and not by the browser: the two the database held at the cut
were `346` bar and `3.4318248339629553` bar, with a `sourceTimestampUtc` a fraction of a
second before the moment each was stored.

**3, the journal in words and in local time.** The `SamplesLost` row — the walk's own loss,
still the newest entry a day later — reads `2026-09-26 21:59:43` under Recorded,
`SamplesLost` under Event, `Bitola/Edge plant-7` under Tag and, in Note, `190 samples
dropped at the source21:57:37 – 21:59:12`. The words are the ones this step asks for: a
count and a window in times a person reads, not column names and not microseconds. The ends
are the loss's own — `21:57:37` is the cut of the walk's own outage and `21:59:12` the last
reading it lost, both local (+02:00), which is the pair `gap_from` and `gap_until` hold
above — and the separator between the sentence and the window is missing, which is the first
finding below. Both `EvaluationStarted` rows are there for a reader scoped to one Site:
`21:53:36` carrying the window it was down for (`21:53:08 – 21:53:36`) and `21:51:37`
carrying none, because the first start this database ever saw had nothing before it to be
missing. That is ADR-0013 read on a screen rather than in a test.

**4, the form knows a driver can push.** Typing `mqtt` into **Driver** took the scan-interval
field away before anything else was typed, and left in its place: "This driver pushes its
values when they change, so there is no scan interval." Nothing was saved. Both screens also
show what the step did not ask about: a new device opens on Modbus's three connection
settings (`host` `127.0.0.1`, `port` `5502`, `unitId` `1`) for a driver that has no use for
them — not a defect, because a form cannot know a driver's settings before it knows the
driver, but three rows to delete by hand every time a pushing device is created.

**What this walk found.** One defect in the client, and one in this document.

- **A Note's parts printed as one word.** The Note column is several parts — "first seen
  after restart", a retirement reason, a source's own sentence, a gap's window — each its own
  `<span class="tag">`, and `.tag` had no rule in `app.css` at all. Angular drops the
  whitespace between two elements, so two parts met with nothing between them, and the loss
  above is where an eye catches it: `at the source21:57:37`. Every row with two parts had it
  — a reason beside a source note, an engine gap beside a count of unrecorded transitions.
  `.tag` now carries the separation it was relying on (`margin-right: 0.4rem`), which
  separates every pair at once. It was found by a person reading a screen: the client's 47
  tests passed before it and after it, and a missing space is not a thing they can see.
- **The procedure promised a reason no screen can show.** Step 5 said the reading "carries
  its reason where the value is". It does not, and the code is explicit that it cannot: a Bad
  reading draws `—` (`tag.ts`), `TagSnapshot` carries no reason for a screen to draw, and a
  **pushing** device that falls silent logs nothing at all. What is seen is a quality, and
  the cause is read elsewhere — the edge's log at step 8, or the Gateway's for a polled
  device. The step is corrected above. Whether a screen *should* say why a pushed tag went
  quiet is a question for a later ADR: today's answer is ADR-0016's and it is deliberate —
  `GatewayApp.cs` gives a driver a logger "so a driver can say why a tag has no value — the
  reason a Bad reading cannot carry on its own".

**What to record:** the three answers the browser gave (what the pushed tags showed and
why, what the journal entry said in words, whether the device form hid the scan
interval); the five numbers of step 11 and the three of step 7 for the second outage;
what the screen showed at the cut, at the staleness limit and on the burst; and anything
that looked wrong or merely confusing, including anything in this section that did not
match what you saw.

**What this does not close.** The first two bullets of the list and the arm64 section's
own closing line: a link between two hosts, two clocks, and a board.

## What to report

Anything that looks wrong or merely confusing, even where the behaviour is
technically correct. Phase 5.5's hand walk found ten defects a green suite had
passed, Phase 6's found four, and this one found six — all six in this document
rather than in the feature, which is the shape a first walk of a feature that has
only ever been tested tends to take. The walk that put a screen in front of the
walker ([below](#since-the-walk-what-the-screen-says)) found a seventh, and that
one was in the client: a Note whose parts printed as one word.

