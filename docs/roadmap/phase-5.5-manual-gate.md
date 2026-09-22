# Walking the Phase 5.5 gate by hand

Phase 5.5's test gate is in [the phase plan](phase-plan.md); the tests cover it, and
they pass. This is the other half: using the thing. Phase 2 found two real defects this
way that a green suite had said nothing about — a chart drawing a straight line through
a gap, and a form field shadowing a DOM property.

Everything below has been read out of the code rather than remembered. Where a value is
a default it says so, with the setting that changes it.

[Running locally](phase-1-running.md) has the full setup; this repeats only what the
gate needs, in order, for PowerShell.

## 0. Before you start

One thing to know, because it looks like a bug and is not: **the Journal screen reads
once**, when you open it and when you press *Refresh*. History does not change under a
reader, so it is deliberately not wired to the live push. After a Gateway restart you
have to press *Refresh* to see the new rows.

## 1. Database, migrator

```powershell
cd C:\GITProjects\SCADA_DARBOX
docker compose up -d

$env:SCADA_MIGRATOR_CONNECTION = "Host=localhost;Port=5432;Database=scada;Username=scada;Password=scada"
$env:SCADA_APP_DB_PASSWORD = "scada_app"
dotnet run --project src/Migrator
```

The Migrator holds the privileged role; the Gateway never migrates and refuses to start
if the schema does not match its build (ADR-0012).

## 2. Simulator

In its own window:

```powershell
cd C:\GITProjects\SCADA_DARBOX
dotnet run --project tools/ModbusSimulator
```

`holding:0` is a discharge pressure in hundredths of a bar: a sine around **4.2 bar with
an amplitude of 0.8**, so it travels between **3.4 and 5.0 bar** with a period of about
50 seconds. That range decides the threshold in step 5.

## 3. Gateway

In its own window. The extra setting is the point of the shelf step — the sweep that
notices an expired shelf runs every **30 seconds by default**, which is a long time to
stand and watch:

```powershell
cd C:\GITProjects\SCADA_DARBOX
$env:SCADA_APP_DB_PASSWORD = "scada_app"
$env:SCADA_INITIAL_ADMIN_USERNAME = "admin"
$env:SCADA_INITIAL_ADMIN_PASSWORD = "choose-at-least-12-chars"
dotnet run --project src/Gateway -- --Alarms:ShelveSweepInterval=00:00:05
```

The initial admin is created only while no user exists and ignored afterwards; drop
those two variables on later runs. The API and hub are on `http://localhost:5220`.

## 4. Web client

```powershell
cd C:\GITProjects\SCADA_DARBOX\src\Web
npm install
npm start
```

`http://localhost:4200`. Sign in as `admin`.

## 5. Give a tag a threshold that always alarms

*Browse* → pick the pressure tag → **Alarm threshold** → **Add**.

Set **High limit** to `3.0` and leave Low blank. The simulator's floor is 3.4 bar, so the
value never comes back inside the limit and the alarm stays Active — which is what the
shelf step needs. (A high limit of `4.5` instead makes it alarm and clear every ~50
seconds; useful for watching a new occurrence start, not for this gate.)

Only an Admin sees this form. Save, and the alarm appears in the banner within a scan.

## 6. A Viewer on the other Site

*Users* → fill **User name** and a password → create. Then, in that user's row, set the
role **on the second Site only** to `Viewer`, leaving the Site the alarm is on as `None`.
Keep the credentials; step 10 uses them.

## 7. Acknowledge, restart, and look again

1. In the banner, **Acknowledge** the alarm. It stays listed, now marked as seen by you.
2. Stop the Gateway window with `Ctrl+C` — a clean stop, so it journals
   `EvaluationStopped`.
3. Start it again with the same command as step 3, without the two admin variables.
4. Reload the browser and sign in again.

**What must be true:** the alarm is still listed, still `Acknowledged`, and still names
**your** username as the acknowledger. Nothing about it should say it was first seen
after the restart — the engine knew about it before, and it says so only for what it
genuinely first observed on the other side of the gap.

## 8. A shelf that ends by itself

The **Shelve 1 h** button in the banner is fixed at 60 minutes — there is no duration
field in the client. For a shelf you can watch expire, call the API directly. In a
fourth window:

```powershell
$login = Invoke-RestMethod -Method Post -Uri http://localhost:5220/api/auth/login `
  -ContentType 'application/json' `
  -Body '{"username":"admin","password":"choose-at-least-12-chars"}'

$headers = @{ Authorization = "Bearer $($login.token)" }

# The definition id is on the alarm itself.
$alarm = (Invoke-RestMethod -Uri http://localhost:5220/api/alarms -Headers $headers)[0]

Invoke-RestMethod -Method Post -Headers $headers `
  -Uri "http://localhost:5220/api/alarms/$($alarm.definitionId)/shelve" `
  -ContentType 'application/json' -Body '{"durationMinutes":1}'
```

One minute is the shortest the server accepts: a shelve must be positive, and
`durationMinutes` is a whole number. The maximum is 24 hours by default
(`Alarms:MaxShelveDuration`).

**What must be true:** the alarm shows `Shelved` and leaves the interrupting banner.
About a minute later — plus up to one sweep, five seconds with the setting in step 3 —
it returns to **Active** on its own, with no click from anyone. The value never went
back in range; the shelf simply ended.

## 9. The outage in the journal

Open **Journal** and press **Refresh**.

**What must be true:** the restart from step 7 reads as an `EvaluationStopped` followed
by an `EvaluationStarted` — a stated period during which nothing was being evaluated,
not a blank stretch you have to infer. The `Shelved` and `Unshelved` rows from step 8
are there too, the `Unshelved` with no actor, because nobody did it.

Those two engine rows carry no tag and no Site. An outage applies to the whole Gateway,
not to one piece of equipment.

## 10. The same screen as the Viewer

Sign out, sign in as the Viewer from step 6, and open **Journal**.

**What must be true, both halves:**

- The outage is **still there**. `EvaluationStarted` and `EvaluationStopped` belong to no
  Site, and a Viewer on one Site still has to know the system was not watching. If they
  are missing, the Site filter is dropping null rows and an outage reads as a quiet
  period — the exact failure ADR-0013 exists to prevent.
- The alarm rows from the other Site are **not** there, and neither is the alarm in the
  banner. "No Site means everyone's" must not have leaked into "any Site is everyone's".

## What to report

Anything that looks wrong or merely confusing, even where the behaviour is technically
correct. A screen that is right and unreadable still fails an operator at four in the
morning.
