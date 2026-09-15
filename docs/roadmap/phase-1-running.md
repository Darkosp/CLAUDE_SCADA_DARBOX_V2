# Running Phase 1 locally

Phase 1 is the vertical slice from the [phase plan](phase-plan.md): a simulated Modbus
device through the driver, tag engine and historian to a live value in the browser.

Since Phase 5 the Gateway requires a login and never migrates the database itself
(ADR-0011, ADR-0012), so running it takes one more step than it used to: the
Migrator, before the Gateway.

## Prerequisites

- .NET SDK 10 (LTS)
- Node.js 22+ and npm
- Docker, for the database

## 1. Database

```
docker compose up -d
```

Brings up PostgreSQL 17 with TimescaleDB on `localhost:5432` (database `scada`, user
`scada`, password `scada`). `scada` is the privileged role: only the Migrator uses it.

Only plain hypertables are used. Continuous aggregates and native compression are
TSL-licensed and remain out of scope pending the legal check recorded in ADR-0006.

## 2. Migrator

Applies the numbered scripts under `src/Persistence.TimescaleDb/migrations/` (ADR-0007)
and gives the unprivileged application role, `scada_app`, its password. Run it after
pulling any change that adds a migration; running it again is a no-op.

Both values come from environment variables only. In PowerShell:

```powershell
$env:SCADA_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=scada;Username=scada;Password=scada"
$env:SCADA_APP_DB_PASSWORD = "scada_app"
dotnet run --project src/Migrator
```

In bash, the same with `export NAME=value`.

`scada_app` is also what the test suites use as the application role's password unless
`SCADA_APP_DB_PASSWORD` says otherwise. The role belongs to the whole database server,
so a different password here and in the tests would have each overwrite the other.

Schema changes are always a new numbered script, never a hand-applied `ALTER`.

## 3. Simulated device

```
dotnet run --project tools/ModbusSimulator
```

Listens on `127.0.0.1:5502` as Modbus unit 1:

| Address | Meaning |
|---|---|
| `holding:0` | discharge pressure, hundredths of a bar |
| `coil:0` | pump running |

## 4. Gateway

The Gateway connects as `scada_app`. Its connection string in `appsettings.json` has no
password; it comes from `SCADA_APP_DB_PASSWORD`. It seeds one tenant, two sites, one
device and two tags into an empty database.

No account ships with it. The first time, supply the initial Admin — it is created only
while no user exists, and ignored after that:

```powershell
$env:SCADA_APP_DB_PASSWORD = "scada_app"
$env:SCADA_INITIAL_ADMIN_USERNAME = "admin"
$env:SCADA_INITIAL_ADMIN_PASSWORD = "<at least 12 characters>"
dotnet run --project src/Gateway
```

(`--initial-admin-username` and `--initial-admin-password` on the command line work too.)
Remove the two admin variables once the account exists.

The Gateway refuses to start, and says why, if the Migrator has not been run for this
build's migrations, or if it is pointed at the privileged `scada` role.

Serves the API and the SignalR hub on `http://localhost:5220`.

- `POST /api/auth/login` with `{ "username": ..., "password": ... }` — returns a token;
  send it as `Authorization: Bearer <token>` on every other request
- `GET /api/tags` — every tag on a Site you can see, with its current value
- `GET /api/tags/{id}/history?from=&to=` — historised samples for one tag
- `/hubs/tags` — real-time push, per Site

## 5. Web client

```
cd src/Web
npm install
npm start
```

Opens on `http://localhost:4200` and shows the live tag values.

> **On `phase-5/permissions` the web client has no login yet**, so against this
> Gateway it receives 401s. That is the next piece of Phase 5.

## Checking the test gate

The gate is met when a value from the simulator reaches both the historian and the
browser. With all five running:

1. The browser shows *Discharge Pressure* moving around 4.2 bar and *Pump Running*
   toggling.
2. `GET /api/tags/{pressure-tag-id}/history`, with a token, returns rows whose
   `sourceTimestampUtc` and `ingestedAtUtc` differ.
3. Stopping the simulator turns both tags to `Bad` quality in the browser — an offline
   device reads as unreadable, not as zero (ADR-0003).
