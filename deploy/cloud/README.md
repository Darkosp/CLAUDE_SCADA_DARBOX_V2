# Running SCADA DARBOX in the cloud topology

This guide covers a Gateway in the cloud, with an edge agent at each plant. The edge agents read
the plant's devices and send what they read to the cloud through an MQTT broker. For everything on
one machine at the plant, use [`deploy/README.md`](../README.md) instead. That guide is also the
reference for anything this one does not repeat: backups, and going back after a failed upgrade.

Every command below is for **bash**, run from the **root of the repository**. On a Linux server
that is the ordinary shell; on Windows it is Git Bash. Every command was run as written before it
was put here, with different project names and ports so it could not touch a running installation.

**In Git Bash,** first run `export MSYS_NO_PATHCONV=1`. Without it, Git Bash rewrites a path
inside a container, such as `/mosquitto/audit/refused.log`, into a Windows path, and the command
fails with "No such file". It happened while this guide was being checked. (`certs.sh` undoes the setting for
itself: `openssl` there needs its file paths converted.)

## What you get

**In the cloud** (`deploy/cloud/docker-compose.yml`):

| Container | What it is |
|---|---|
| `timescaledb` | the database: configuration, users, history, the journal |
| `migrator` | prepares the database for this version, then stops (`Exited (0)` is correct) |
| `gateway` | the server: stores what the edges send, raises alarms, serves the web page |
| `broker` | Mosquitto: where the edges publish and the Gateway subscribes |

**At each plant** (`deploy/edge/docker-compose.yml`):

| Container | What it is |
|---|---|
| `edge` | reads the plant's devices, and keeps what it read on disk until the broker has it |

### How data gets from a device to the history

1. **The edge reads its devices** on their scan intervals and writes every reading to its buffer
   on disk.
2. **It sends the buffer to the broker, oldest first.** A batch leaves the edge's buffer only when
   the broker has acknowledged it.
3. **The broker holds each batch until the Gateway has stored it,** because the Gateway has a
   persistent session. The broker's store is on disk and survives the broker being killed, and it
   has no cap.
4. **The Gateway acknowledges a batch only after the batch is in the database.** If the database is
   down, the batch is not acknowledged and the broker delivers it again later. It is stored once:
   ingestion is idempotent per (tag, source time).

Each link lets go of data only once the next link has it. An outage of the link, of the Gateway,
or of the database loses nothing and duplicates nothing.

There is one exception. If a link outage lasts so long that it fills the edge's buffer, the oldest
readings are dropped. The drop is not silent: the journal says how many readings were lost, and
from when to when.

### Two listeners

The broker listens on two ports:
- **8883, the edges' port.** It is the only port published. On it, a client's id is replaced by the
  name in its certificate.
- **8884, the Gateway's port.** It is reachable only inside the cloud stack.

Do not publish 8884. The Gateway's queue is kept under the Gateway's own client id. On a published
8884, an edge could connect under that id and the broker would discard everything queued for the
Gateway. This was measured, not assumed.

## Before you start

You need:
- **Docker** on the cloud server and on each plant machine.
- **`openssl`** on the machine where you make certificates. Git Bash includes it.
- **A DNS name for the broker** that the edges can resolve. This guide uses `mqtt.example.com`.
- **The edges' port open inbound on the cloud server:** 8883, or whatever you set as
  `SCADA_BROKER_PORT`.

Build the images once, from a commit. These are the same five images as on premises; the edge
agent is one of them:

```bash
deploy/build-images.sh
```

The last line names the tag. You will need it below, for example `8709d92`.

## Certificates

The link uses TLS, and every client has its own certificate (ADR-0017). There is no shared
password. The **name** in a certificate is that client's identity:
- `scada-gateway` for the Gateway;
- the edge's id for each edge, which is also the topic it may publish under.

Make them on a machine you trust. `CERT_DIR` is where they go:

```bash
export CERT_DIR=$HOME/scada-certs
deploy/cloud/certs.sh ca
deploy/cloud/certs.sh broker mqtt.example.com broker
deploy/cloud/certs.sh client scada-gateway
deploy/cloud/certs.sh client plant-7
deploy/cloud/certs.sh expiry
```

- **The broker's certificate carries two names.** `mqtt.example.com` is what the edges connect to.
  `broker` is what the Gateway uses inside the stack. Both are required.
- **`ca.key` signs every identity in the system.** Keep it on this machine only. No server and no
  edge ever needs it, and the Compose files never mount it.
- **What each host gets:**
  - the cloud server: `ca.crt`, `broker.crt`, `broker.key`, `scada-gateway.crt` and
    `scada-gateway.key`;
  - each plant: `ca.crt`, and its own `<edge-id>.crt` and `<edge-id>.key`.

### Certificates expire, without warning

`certs.sh` makes certificates that last 825 days, and a CA that lasts ten years.
- **The broker's certificate or the Gateway's:** when either expires, **every** edge stops
  delivering at once.
- **An edge's certificate:** that edge stops.

Nothing warns you beforehand. The edges keep buffering, so nothing is lost until their buffers
fill, but nothing arrives either. Put a date in a calendar a month before the earliest expiry. To
see every date:

```bash
deploy/cloud/certs.sh expiry
```

To renew one, make it again with the same name, copy it to its host, and restart that one
container. The CA stays the same, so nothing else has to change.

## Installing the cloud side

### 1. Settings and passwords

```bash
cp deploy/cloud/.env.example deploy/cloud/.env
```

Fill in every value in `deploy/cloud/.env`:
- `SCADA_IMAGE_TAG`: the tag from `build-images.sh`.
- Two database passwords and the first Admin, as in the on-premises guide.
- `SCADA_CERT_DIR`: the directory on this server that holds the five files listed above.

Compose refuses to start while any value is missing, and names the one that is.

### 2. Start

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env up -d
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env ps
```

`migrator` shows `Exited (0)`, and the other three are `Up`. The broker's log ends with
`mosquitto version 2.1.2 running` after it opens both ports:

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env logs broker
```

An empty database gets a demo configuration, as it does on premises. Its Modbus device has no
simulator to read in the cloud, so it reads Bad and its log shows connection errors. Delete that
device in the web client once you have your own.

## Adding an edge

Every edge takes three things: a certificate, a device in the Gateway, and a configuration at the
plant.

### 1. The certificate

Run `deploy/cloud/certs.sh client <edge-id>`, as above. Give the edge a name of letters, digits,
`.`, `_` and `-`. The name cannot begin with `scada-darbox-`: those names are the Gateway's own
client ids, and the script refuses them.

### 2. The device in the Gateway

In the web client, add a device with the driver **mqtt** and these settings:

| Setting | Value |
|---|---|
| `host` | `broker` |
| `port` | `8884` |
| `topic` | `scada/edge/<edge-id>/samples` |
| `tls` | `true` |
| `caFile` | `/app/mqtt/ca.crt` |
| `certFile` | `/app/mqtt/scada-gateway.crt` |
| `keyFile` | `/app/mqtt/scada-gateway.key` |

There is no scan interval: the device pushes. Then add its tags. **Each tag's id is what the edge
needs.** An edge names a reading by the id of the Gateway's tag, never by its name. The Gateway's
log then shows `Subscribed to scada/edge/<edge-id>/samples on broker:8884 (new session)`. After
every later restart it shows `(session resumed)`, and that session is what the broker keeps the
queue under.

Two more settings are optional:
- **`stalenessSeconds`** (default 60): how long a tag may go without a reading before it reads
  Bad.
- **`sessionExpiryHours`** (default 720, which is 30 days): how long the broker keeps queueing for a
  Gateway that is away. Past it, the queue is discarded.

### 3. The edge

At the plant:

```bash
cp deploy/edge/.env.example deploy/edge/.env
```

Fill in `deploy/edge/.env`:

| Variable | Value |
|---|---|
| `SCADA_EDGE_ID` | the edge's name, exactly as its certificate has it |
| `SCADA_BROKER_HOST` | the broker's name, as in its certificate |
| `SCADA_EDGE_CERT_DIR` | the directory with `ca.crt`, `<edge-id>.crt` and `<edge-id>.key` |

That is the whole file. Nothing on the plant machine lists devices, addresses or tag ids: the
cloud derives what this edge reads and publishes it on the edge's own topic, retained, and the
edge subscribes to it over the connection it already holds (ADR-0019). **The cloud's half is
built, and this stack turns it on** (`EdgeProvisioning__Enabled: "true"` in
`deploy/cloud/docker-compose.yml`, with the Gateway's own certificate mounted for it). An edge
with no device assigned is published an empty configuration and therefore reads nothing, which
is a configuration in its own right. **This was walked on 2026-10-01 and again on 2026-10-02**, with
a real broker and a real edge agent accepting a derived configuration end to end, and the numbers
are in `docs/roadmap/phase-7-manual-gate.md`; an edge also states which drivers its own build has,
and the cloud refuses a device it cannot read by name (ADR-0019 §8, the second walk). The edge keeps
the last configuration it accepted across a restart either way. Then:

```bash
docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env up -d
docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env logs edge
```

The log shows `Connected to device …` for each device and `Connected to the broker at …`. Within
seconds, the tags in the web client show values with the times the edge measured them.

**`The broker refused a batch … NotAuthorized` means the edge's id and its certificate disagree.**
The broker lets an edge publish only under the name in its certificate. Nothing is lost in the
meantime: refused batches stay in the edge's buffer.

### If the edge machine is arm64

Nothing above changes. On the edge machine itself, build with no arguments — there is nothing to
cross, the images come out arm64, and they carry the plain tag:

```bash
deploy/build-images.sh
```

To build the edge's image on an x64 server instead, name the architecture. That builds the edge
agent alone — the only image an edge machine runs — and tags it `-arm64`, so it cannot quietly
replace the x64 image under the same tag:

```bash
deploy/build-images.sh HEAD arm64
```

Every build here goes through BuildKit, which Docker Desktop has; on a Linux server the plugin is a
package (`docker-buildx` on Ubuntu, `docker-buildx-plugin` from Docker's own repository), and the
script says so when it is missing. A cross-build for arm64 also needs binfmt, because the image's own
architecture check then runs emulated — the SDK does not; it runs here and cross-publishes:

```bash
docker run --privileged --rm tonistiigi/binfmt --install arm64
```

The result is `edge-agent:<tag>-arm64`; that is what `SCADA_IMAGE_TAG` has to carry on the side that
runs it, and the build is slow — the .NET SDK is running emulated. **Running that image on a machine
that is not arm64 is a check, not a deployment**: it says the publish and the image are right, not
that the hardware is. Side by side with the plain Compose file, which is what a real edge machine
uses:

```bash
docker compose -f deploy/edge/docker-compose.yml -f deploy/edge/docker-compose.arm64.yml \
  --env-file deploy/edge/.env up -d
```

## What is recorded where

**Volumes.** Losing either of the first two loses data:

| Volume | Holds |
|---|---|
| `broker-data` | what the broker is holding for the Gateway |
| `edge-buffer` (at each plant) | what the edge has not yet handed to the broker |
| `broker-audit` | every refused publish, and every connection with the certificate behind it |

**The audit file.** It records which certificate tried which topic:

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env exec broker cat /mosquitto/audit/refused.log
```

A refusal looks like this:

```
2026-09-25T12:50:26: New client connected from 172.20.0.1:41026 as plant-8 (p5, c1, k15, u'plant-8').
2026-09-25T12:50:26: Denied PUBLISH from plant-8 (d0, q1, r0, m1, 'scada/edge/plant-7/samples', ... (257 bytes))
```

This is the **broker's** record: the Gateway never sees a refused publish. Mosquitto logs a refusal
only at its most verbose level, so the broker's entrypoint reads that whole log and keeps only
refusals and connections, on their own volume. The container's own log leaves out the per-packet
lines.

**Sizing the broker's disk.** The broker's queue has no cap on purpose. Mosquitto's default cap
discards messages silently past 1000, which is the very hole the edge buffer exists to prevent.
The limit is therefore the disk. A batch is at most 500 readings of about 200 bytes. A Gateway
that is away for a day, with edges sending 100 readings a second between them, leaves roughly
2 GB queued.

## Upgrading

Build the new images, change `SCADA_IMAGE_TAG` in both `.env` files, and run `up -d` on each side,
as in the on-premises guide. Take a database backup first.

The Gateway can be down while it is upgraded. The broker holds what arrives and delivers it when
the Gateway is back. This was measured: the Gateway stopped for 40 seconds, with no gap and no
duplicate afterwards.

Upgrade the cloud before the edges. If the edges' message format ever changes, a newer edge
talking to an older Gateway would have its messages refused. This is a precaution for the future,
not a problem today: the format has one version, 2.

## Stopping

**Stopping and deleting.** `down` keeps every volume; `down -v` deletes them all, with everything
held in them.

```bash
docker compose -f deploy/cloud/docker-compose.yml --env-file deploy/cloud/.env down
docker compose -f deploy/edge/docker-compose.yml --env-file deploy/edge/.env down
```

**Stopping an edge.** Stop an edge by stopping its container. Its buffer is kept on disk, and the
edge resumes where it left off.
