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

In the banner, pick **5 min** from the duration list beside the alarm, then press
**Shelve**. The button stays disabled until a duration is chosen: nothing is sent on
behalf of an operator who has not picked one.

Five minutes is the shortest offered. Shorter than that is no use to someone changing a
sensor, and the list — 5 min, 15 min, 1 h, 4 h, 8 h, 24 h — is deliberately a set of
choices rather than a free field, where a slip of the keyboard becomes a silence nobody
meant. The real limit is the server's 24 hours (`Alarms:MaxShelveDuration`); the list
stays inside it but is not what enforces it.

**What must be true:** the alarm shows `Shelved` and leaves the interrupting banner.
About five minutes later — plus up to one sweep, five seconds with the setting in step 3
— it returns to **Active** on its own, with no click from anyone. The value never went
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

## The result

**Walked, and it found ten things no test had. All ten were fixed and re-checked on screen
before the phase merged.** The procedure above is what was followed; this section is what it
came back with, and it is written here rather than only in the phase plan because a record of
*how* to walk a gate with no record of what the walk found reads as a gate nobody walked.

**The first one stopped the walk at its first step, and the other nine are all the same kind of
thing** — the screen failing an operator while behaving exactly as specified. That distinction is
the whole reason this file exists: a suite can be green and an operator can still be stuck.

### The one that stopped it

**Saving an alarm threshold threw `raw.trim is not a function`.** An `<input type="number">` bound
with `ngModel` hands over a **number** — or `null` once the box is empty or its contents are not a
number — and never the string the draft was declared as. Declaring the field `string` is a lie the
compiler cannot catch, and it took the save with it.

**Fixing it exposed a quieter defect behind it:** with the value arriving as a number, non-numeric
text silently became **"no limit"** rather than a refusal. Those are different things — *no limit on
this side* against *the limit is zero* — and zero is an ordinary number. A blank box and a box
holding something unreadable must not mean the same thing.

`NumberField` and `parseLimit` are the fix, and they carry the reasoning; the field is now typed
for what it really holds. The tests are in `parse-number-field.test.mjs`.

### The other nine — correct behaviour an operator could not use

| What the walk saw | What it was |
| --- | --- |
| the banner showed the value at raise without saying so | a number with no label reads as the *current* value, and on a cleared alarm the two disagree |
| two words for one state | the same state named differently in two places |
| the acknowledging user was visible only in the journal | the list a reader actually watches did not say who acknowledged |
| no way to shelve an alarm once acknowledged | shelving was reachable only while an alarm was Active, so acknowledging it first — the normal order — removed the control |
| a shelf duration that stayed selected after use | the next shelf silently reused it; a duration left sitting in a box is that shelf's default |
| raw doubles | `4.8100000000000005` in a Note column, which is arithmetic rather than a measurement |
| an outage window without its date across midnight | `15:31:33 – 14:13:44` reads as an interval that ran *backwards*; it was an outage of nearly a day |
| the journal stacking above Browse | two views at once, one over the other |
| internal slugs in the Note column | the engine's own vocabulary where a reader expects a sentence |

Each fix is in the client, each is held by a test, and the four that are pure formatting live in
`journal-formatting.test.mjs` with a comment saying so — two of those tests name this walk in their
own text ("exactly what the gate saw in the Note column"). The slug formatter is
`source-events.test.mjs`.

**The lesson, and it is the one this project keeps relearning.** Phase 2 found two defects this way
and said the same thing; Phase 8's walk found four more on 2026-10-05 and said it again. **A green
suite says nothing about whether a screen is usable**, and the defects a walk finds are
disproportionately the ones that need a person: a label that is missing, a control that has gone
away, a number with more digits than meaning.

## What remains open from this phase

**Nothing. Both deferred items were closed on 2026-10-05.** They are recorded here because this
file's job is to say what the walk left behind, and leaving the entry reading "deferred" once they
were done is exactly the kind of stale present-tense claim this project corrects in place.

- **Filtering the journal.** A thousand rows in a browser is not how anyone finds one alarm's
  history. Now server-side — by tag and by event type — because the Site filter and the row limit
  already were, and because filtering in the browser would answer "which of the newest two hundred
  rows are about this tag" rather than the question being asked.
- **Flapping** — deadband and on-delay. Closed by
  [ADR-0025](../architecture/decisions/0025-an-alarm-waits-before-it-announces-itself.md), which was
  written before any code as this entry said it had to be, since each changes what an alarm *is*.
