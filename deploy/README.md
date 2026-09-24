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
