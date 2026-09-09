# Running Phase 1 locally

Phase 1 is the vertical slice from the [phase plan](phase-plan.md): a simulated Modbus
device through the driver, tag engine and historian to a live value in the browser.

## Prerequisites

- .NET SDK 10 (LTS)
- Node.js 22+ and npm
- Docker, for the database

## 1. Database

```
docker compose up -d
```

Brings up PostgreSQL 17 with TimescaleDB on `localhost:5432` (database `scada`, user
`scada`, password `scada`). The gateway creates its own schema on first start and seeds
one tenant, one site, one device and two tags — configuring these through the UI is
Phase 2.

Only plain hypertables are used. Continuous aggregates and native compression are
TSL-licensed and remain out of scope pending the legal check recorded in ADR-0006.

## 2. Simulated device

```
dotnet run --project tools/ModbusSimulator
```

Listens on `127.0.0.1:5502` as Modbus unit 1:

| Address | Meaning |
|---|---|
| `holding:0` | discharge pressure, hundredths of a bar |
| `coil:0` | pump running |

## 3. Gateway

```
dotnet run --project src/Gateway
```

Serves the API and the SignalR hub on `http://localhost:5220`.

- `GET /api/tags` — every configured tag with its current value
- `GET /api/tags/{id}/history?from=&to=` — historised samples for one tag
- `/hubs/tags` — real-time push

## 4. Web client

```
cd src/Web
npm install
npm start
```

Opens on `http://localhost:4200` and shows the live tag values.

## Checking the test gate

The gate is met when a value from the simulator reaches both the historian and the
browser. With all four running:

1. The browser shows *Discharge Pressure* moving around 4.2 bar and *Pump Running*
   toggling.
2. `GET /api/tags/{pressure-tag-id}/history` returns rows whose `sourceTimestampUtc`
   and `ingestedAtUtc` differ.
3. Stopping the simulator turns both tags to `Bad` quality in the browser — an offline
   device reads as unreadable, not as zero (ADR-0003).
