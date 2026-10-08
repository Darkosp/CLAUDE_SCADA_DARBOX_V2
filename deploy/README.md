# Installing and running SCADA DARBOX on one machine

This folder runs the whole system on a single server with Docker. It is written for someone
setting it up for the first time; you do not need to know how the system is built inside.

It lives here, next to the files it describes, rather than with the project's design notes.
The Compose file, the settings template and this guide change together.

Every command below is for **Windows PowerShell**, run from the **root of the repository**
(the folder that contains `deploy\`). Every one of them was run as written before it was put
here.

## What you get

Five containers, started together:

| Container | What it is |
|---|---|
| `timescaledb` | the database: configuration, users, value history, the alarm journal |
| `migrator` | prepares the database for this version, then stops. It is *meant* to show `Exited (0)` |
| `gateway` | the server: talks to devices, raises alarms, and serves the web page you open |
| `modbus-sim`, `opcua-sim` | two simulated devices, so there is something to watch before real equipment is connected |

You reach everything through one address: `http://127.0.0.1:<port>` on the server itself, or
the server's name or IP address from another computer on the network.

## Before you start

You need:

- **Docker Desktop**, running.
- **Git for Windows**. Its `bash` builds the images.
- A copy of this repository (`git clone`).
- **A clock that is right, and kept right by NTP.** Every reading carries the time of the machine that
  measured it, and the Gateway stores that time rather than its own — so a host whose clock has drifted
  writes history that is wrong about *when*, permanently and silently. This matters more on an edge than in
  the cloud: the edge agent keeps measuring while the link is down, and its readings are stored later with
  their own timestamps, which is the whole point of the buffer (ADR-0017). The Gateway **notices** a source
  whose clock disagrees with it and journals the difference (`SourceClockSkew`), which is a report and not a
  correction: nothing here adjusts a reading's time, because a corrected timestamp is a guess about a moment
  nobody observed. Check it with `w32tm /query /status` on Windows, or `timedatectl` on Linux, and make the
  host sync before it is trusted with a plant's history.

### 1. Check that the port is free

The system uses port **8080** unless you choose another. Check it **before** starting anything:

```powershell
Get-NetTCPConnection -State Listen -LocalPort 8080 -ErrorAction SilentlyContinue |
  Select-Object LocalAddress, LocalPort, OwningProcess, @{ Name = 'Process'; Expression = { (Get-Process -Id $_.OwningProcess).ProcessName } }
```

It must print **nothing**. (`netstat -ano | Select-String ":8080"` shows the same without the
program names.) If anything is listed, choose another port in step 3 and use it everywhere.

**Why this matters.** Docker does not always refuse a port that another program already holds.
It can start without a word of complaint, and then two programs listen on the same port. One
of them listens on IPv4 (`127.0.0.1`) and the other on IPv6 (`::1`). On Windows the name
`localhost` often goes to IPv6 first. The browser then reaches the *other* program and shows
its error page, while the system itself is running fine.

On the machine this was first installed on, an unrelated `ApplicationWebServer` held 8080. The
browser showed **"Access Error: 404"**. That page came from the other program; SCADA DARBOX
never saw the request.

Two habits prevent it: check the port first, and always open **`127.0.0.1`**, never
`localhost`.

Once the system is running, the same command shows Docker's own listeners
(`com.docker.backend`, and often `wslrelay` on `::1`). Those are expected; before `up` there
must be none.

## How the Gateway is reached, and why it will not start until you say

**This is the one setting with no default, and it is deliberate (ADR-0028).** Everything an operator
does crosses this connection: the password they sign in with, and the session token every request
afterwards carries — and an Operator can write to a plant. A Gateway that quietly fell back to plain
HTTP would be secure only for as long as whoever installed it happened to remember.

So it **refuses to start** until it is told one of two things, and the refusal names both:

```
This Gateway has no TLS certificate and has not been told that anything in front of it
terminates TLS, so it would serve sign-ins and session tokens in the clear (ADR-0028).
  Give it a certificate:  Server__CertificatePath and Server__KeyPath (PEM).
  Or say what is in front: Server__TlsTerminatedUpstream=true.
```

### Either: give it a certificate

Put the PEM pair in a directory on the host and name it in `.env`:

```
SCADA_TLS_DIR=/etc/scada-darbox/tls      # the host directory holding them
SCADA_TLS_CERT=/app/tls/server.crt       # the path INSIDE the container
SCADA_TLS_KEY=/app/tls/server.key
SCADA_HTTPS_PORT=8443
```

The Gateway then serves HTTPS and **redirects HTTP to it**, so an operator typing a bare host name
still arrives. Both ports stay published for that reason.

### Or: say that something in front terminates TLS

```
SCADA_TLS_TERMINATED_UPSTREAM=true
```

Serving HTTP is then a choice, and the Gateway **says so in its startup log every time** — a
deployment's security posture has to be legible from its own logs, because an absence reads the same
whether it was chosen or forgotten.

### Which certificate

**This project does not make it for you, and that is not an omission.** `deploy/cloud/certs.sh` makes
the broker's and the edges' certificates because both ends of that link are ours. A browser's trust is
not ours: it belongs to your organisation's CA, or to a public one.

| What you have | What to do |
|---|---|
| A certificate from your IT department's CA | Use it. Operators' machines already trust that CA |
| A public name and a reachable host | A publicly trusted certificate (Let's Encrypt or similar) |
| Neither — a plant LAN with no CA | A self-signed certificate works, **and every browser will warn** until each machine is told to trust it. Plan that in, or ask for an internal CA |

**A self-signed certificate is not a setting you can hide behind.** Measured on 2026-10-07: with one,
a browser refuses the page outright rather than warning gently. It is better than plain HTTP — the
connection is still encrypted — but it is a thing your operators will meet on day one.

### HSTS is off, on purpose

`SCADA_TLS_HSTS=true` turns it on, and most advice says it should be the default. It is not, here.

HSTS is **remembered by the browser and cannot be overridden by the person using it**. A plant on a
self-signed or internal-CA certificate would hand its operators a browser that refuses the screen with
no way past — during an incident, on the machine that matters. Turn it on when the certificate is
publicly trusted and not before.

### Upgrading an installation that has been serving HTTP

**It will stop.** This is the only breaking change in the project so far, and it is one where
continuing quietly is worse than stopping loudly: add one of the two settings above to `.env` before
`docker compose up -d`, and the upgrade proceeds as it always has.

## Talking to an OPC UA server, and the second breaking change

Everything above is about how **people** reach this Gateway. This is about how the Gateway reaches
**plant equipment**, and since ADR-0033 it is no longer in the clear.

**An OPC UA device now negotiates a secured channel by default, and refuses a server that offers
none.** Before that it connected with no security and an anonymous identity, always, with no way to
ask for anything else — so every value read from a plant crossed the network unencrypted and
unauthenticated.

### What this means for an installation that is already running

**A device pointed at a server that speaks no security will stop connecting**, and its tags will read
Bad with a reason that names the setting. That is deliberate, and it is the second breaking change in
this project for the same reason as the first: continuing quietly is worse than stopping loudly.

Two settings on the device decide it, both added through *Browse* → the device → **Edit device**:

| Setting | Default | What it means |
|---|---|---|
| `security` | `required` | The strongest endpoint the server offers. A server offering only `None` is refused. |
| `security` | `none` | An unsecured session, chosen deliberately. Logged at every connect. |
| `acceptUntrustedCertificates` | `false` | The server's certificate must be in the Gateway's trusted store. |
| `acceptUntrustedCertificates` | `true` | Any server certificate is accepted. Logged at every connect, with its subject and thumbprint. |

**These are two different questions and it is worth keeping them apart.** `security` is *is this
channel encrypted*. `acceptUntrustedCertificates` is *do we check who is on the other end of it*. A
secured channel to an unverified server still stops anyone reading the traffic; it does not stop
someone standing in the middle of it.

### Trusting a server properly

Put the server's certificate (DER or PEM) in the Gateway's trusted store, which lives on the
`gateway-pki` volume and survives upgrades and container re-creation:

```bash
docker compose cp ./plant-server.der gateway:/app/pki/trusted/certs/plant-server.der
```

Then restart the Gateway. The device connects with `acceptUntrustedCertificates` left at `false`, and
nothing is trusted that you did not put there.

### What a refusal looks like

The tag reads Bad — it does **not** show a stale number (ADR-0003) — and the Gateway's log names which
of the two problems it is, because the remedies have nothing in common:

- `the server's certificate is not trusted: subject '...', thumbprint ...` — go and trust it, or decide
  not to.
- `The OPC UA server at '...' offers no secured endpoint` — configure the server, or set
  `security=none` and mean it.
- `the connection could not be established: ...` — the machine is not answering. A different errand
  entirely, and before ADR-0033 all three said the same thing.

### The simulator

`opcua-sim` offers a secured endpoint and its certificate is self-signed, so the demo device needs
`acceptUntrustedCertificates=true` and **not** `security=none`. That is the right way round: the
channel is real, and only the identity check is relaxed, for a server you started yourself.

## Installing

### 2. Build the images

```powershell
git pull
& "C:\Program Files\Git\bin\bash.exe" deploy/build-images.sh
```

This takes a few minutes the first time. It builds from the last **commit**: changes you have
not committed are left out, and it says so. The last line names the version, a short code
such as `42eb40d`:

```
Built scada-darbox/{migrator,gateway,modbus-sim,opcua-sim}:42eb40d from 42eb40d5cb4c...
```

Write that code down; the next step needs it.

### 3. Settings and passwords

```powershell
Copy-Item deploy/.env.example deploy/.env
notepad deploy/.env
```

| Setting | What to put there |
|---|---|
| `SCADA_IMAGE_TAG` | the code from step 2 |
| `SCADA_DB_ADMIN_PASSWORD` | a new password for the database's administrator account |
| `SCADA_APP_DB_PASSWORD` | a new password for the account the server uses in the database |
| `SCADA_INITIAL_ADMIN_USERNAME` | the name you will sign in with the first time |
| `SCADA_INITIAL_ADMIN_PASSWORD` | its password: at least 12 characters |
| `SCADA_HTTP_PORT` | 8080, or the free port you chose in step 1 |

**You choose every password.** None is supplied, and nothing starts until the first three are
filled in: the error names the one that is missing. Passwords must not contain `;`.

To make a strong one:

```powershell
$bytes = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes); [BitConverter]::ToString($bytes).Replace('-', '').ToLower()
```

Keep `deploy/.env` safe and out of version control; it is ignored by git on purpose. Keep a
copy of the two database passwords somewhere safe as well: restoring a backup needs them.

### 4. Start

```powershell
docker compose -f deploy/docker-compose.yml up -d
docker compose -f deploy/docker-compose.yml ps -a
```

What you should see:

```
SERVICE       STATUS
gateway       Up ...
migrator      Exited (0) ...
modbus-sim    Up ...
opcua-sim     Up ...
timescaledb   Up ... (healthy)
```

`migrator` **Exited (0)** is correct: it prepares the database and stops, and the server does
not start until it has.

### 5. First sign-in

Open **`http://127.0.0.1:8080`** (your port, if you changed it) and sign in with
`SCADA_INITIAL_ADMIN_USERNAME` and `SCADA_INITIAL_ADMIN_PASSWORD`.

Then **remove the first-admin settings**. The account now exists and stays; the settings are
only used while there is no user at all. Leaving them in place would keep the password in the
server's environment. Empty both lines in `deploy/.env`:

```
SCADA_INITIAL_ADMIN_USERNAME=
SCADA_INITIAL_ADMIN_PASSWORD=
```

and apply the change:

```powershell
docker compose -f deploy/docker-compose.yml up -d
docker compose -f deploy/docker-compose.yml exec gateway printenv SCADA_INITIAL_ADMIN_PASSWORD
```

The second command must print an **empty line**. You still sign in exactly as before.

## Where the data lives

Everything that must survive a restart or an upgrade is kept in three Docker **volumes**,
outside the containers. Containers can be deleted and re-created at will; volumes are the
system's memory.

```powershell
docker volume ls --filter name=scada-darbox_
```

| Volume | What it holds | If it were lost |
|---|---|---|
| `scada-darbox_db-data` | the whole database: devices and tags, users and roles, all value history, the alarm journal, the audit log | everything. Only a backup brings it back |
| `scada-darbox_gateway-pki` | the server's OPC UA certificate and the list of device certificates it trusts | a new certificate is made. Any OPC UA device set to trust the old one must be told to trust the new one |
| `scada-darbox_gateway-keys` | an internal key ring the web framework keeps. Nothing in the system relies on it; on a volume it is created once and not again on every start | a new one is created, silently |

These folders may be empty until they are first needed: `pki` fills when the first OPC UA
device connects.

## Stopping, and the one command that deletes everything

```powershell
docker compose -f deploy/docker-compose.yml down
```

stops and removes the **containers**. The volumes stay. `up -d` brings everything back exactly
as it was: history, alarms, users, certificate.

> **`down -v` deletes everything.**
> `docker compose -f deploy/docker-compose.yml down -v` also removes all three volumes: every
> recorded value, the alarm journal, all users and roles, all device configuration, and the
> OPC UA certificate. There is no undo and no recycle bin. What remains afterwards is a
> system as it was before step 4. Only a backup (below) can bring the data back.
> Use `-v` only to throw an installation away on purpose.

## Backup

Take one regularly, and **always before an upgrade**. It is the only way back from an upgrade
that went wrong. The database only ever moves forward: each version adds changes to it and
none takes them out again. So an older version cannot run on a newer database. Going back
means restoring the database as it was before (see "Going back after a failed upgrade").

```powershell
New-Item -ItemType Directory -Force backups | Out-Null
$stamp = Get-Date -Format yyyyMMdd-HHmmss
docker compose -f deploy/docker-compose.yml exec -T timescaledb pg_dump -U scada -d scada --format=custom --file=/tmp/scada.dump
docker compose -f deploy/docker-compose.yml cp timescaledb:/tmp/scada.dump "backups/scada-$stamp.dump"
docker compose -f deploy/docker-compose.yml exec -T timescaledb unlink /tmp/scada.dump
Get-Item "backups/scada-$stamp.dump"
```

The system keeps running while it is taken. It prints warnings about **"circular foreign-key
constraints"** on `hypertable`, `chunk` and `continuous_agg`. That is normal for this database
and does not affect a full backup like this one.

The file lands in `backups\` in the repository folder (ignored by git). Copy it **somewhere
else as well**, another disk or another machine. A backup on the same disk as the database is
lost together with it.

## Upgrading to a new version

1. **Back up**, as above. Note the file name.
2. **Note the current version**, the `SCADA_IMAGE_TAG` in `deploy/.env`. You need it if you have
   to go back.
3. **Get and build the new version:**

   ```powershell
   git pull
   & "C:\Program Files\Git\bin\bash.exe" deploy/build-images.sh
   ```

4. **Put the new code** from the last line into `SCADA_IMAGE_TAG` in `deploy/.env`.
5. **Start it:**

   ```powershell
   docker compose -f deploy/docker-compose.yml up -d
   docker compose -f deploy/docker-compose.yml ps -a
   docker compose -f deploy/docker-compose.yml logs migrator
   ```

   Every service should show the new version, and `migrator` **Exited (0)**. Its log ends with
   `Schema is at <n> migrations`, and lists any changes it applied to the database.

   The server waits for the migrator to finish before it starts. If the database changes take
   a while, the page is unavailable for that time.

## Going back after a failed upgrade

This puts the database back exactly as it was when the backup was taken. **Everything recorded
after the backup is lost**: values, alarms and configuration changes. That is why the backup
is taken immediately before the upgrade.

1. **Stop the server** and put the database back (replace the file name with yours):

   ```powershell
   $backup = "backups/scada-20260924-070220.dump"
   docker compose -f deploy/docker-compose.yml stop gateway
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -U scada -d postgres -c "DROP DATABASE scada WITH (FORCE)" -c "CREATE DATABASE scada"
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -U scada -d scada -c "CREATE EXTENSION IF NOT EXISTS timescaledb" -c "SELECT timescaledb_pre_restore()"
   docker compose -f deploy/docker-compose.yml cp $backup timescaledb:/tmp/scada.dump
   docker compose -f deploy/docker-compose.yml exec -T timescaledb pg_restore -U scada -d scada /tmp/scada.dump
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -U scada -d scada -c "SELECT timescaledb_post_restore()"
   docker compose -f deploy/docker-compose.yml exec -T timescaledb unlink /tmp/scada.dump
   ```

   `timescaledb_pre_restore` and `timescaledb_post_restore` each print `t`. A notice that the
   extension "already exists, skipping" is normal. `pg_restore` prints nothing when it
   succeeds.

2. **Put the previous version back** in `SCADA_IMAGE_TAG` in `deploy/.env`, the one you noted
   before upgrading, and start:

   ```powershell
   docker compose -f deploy/docker-compose.yml up -d
   docker compose -f deploy/docker-compose.yml ps -a
   ```

The images of the previous version must still exist. `docker images scada-darbox/gateway`
lists them; if they were removed, check out that version and build it again as in step 2.

**Do not start the new version against the restored database.** It is not built for the old
database layout, and it refuses to start, saying which version it expected. The old version,
on the other hand, starts normally.

## Restoring onto a new machine

When the old server is gone and only the backup survived:

1. Install as above, steps 1–3, **with the same two database passwords** as before, and the
   same `SCADA_IMAGE_TAG` as when the backup was taken.
2. Start **only the database**, wait until it is ready, and restore into it:

   ```powershell
   $backup = "backups/scada-20260924-070220.dump"
   docker compose -f deploy/docker-compose.yml up -d --wait timescaledb
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -U scada -d postgres -c "DROP DATABASE scada WITH (FORCE)" -c "CREATE DATABASE scada" -c "CREATE ROLE scada_app NOLOGIN"
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -U scada -d scada -c "CREATE EXTENSION IF NOT EXISTS timescaledb" -c "SELECT timescaledb_pre_restore()"
   docker compose -f deploy/docker-compose.yml cp $backup timescaledb:/tmp/scada.dump
   docker compose -f deploy/docker-compose.yml exec -T timescaledb pg_restore -U scada -d scada /tmp/scada.dump
   docker compose -f deploy/docker-compose.yml exec -T timescaledb psql -U scada -d scada -c "SELECT timescaledb_post_restore()"
   docker compose -f deploy/docker-compose.yml exec -T timescaledb unlink /tmp/scada.dump
   ```

   `--wait` matters. Without it the next command runs while the new database is still setting
   itself up, every restore step fails, and the migrator then prepares an **empty** database
   instead.

   `CREATE ROLE` is needed here and not in the previous section. On a new machine the account
   the server uses does not exist yet.

3. Start everything:

   ```powershell
   docker compose -f deploy/docker-compose.yml up -d
   docker compose -f deploy/docker-compose.yml logs migrator
   ```

   The migrator says `No new scripts need to be executed` and sets the server's database
   password. You sign in with the same users as before.

The OPC UA certificate is not part of the database backup. On a new machine the server makes a
new one, and devices that trusted the old certificate must be told to trust the new one.
