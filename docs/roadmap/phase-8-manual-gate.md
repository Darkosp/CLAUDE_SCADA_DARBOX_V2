# Walking the Phase 8 gate by hand

**Walked on 2026-10-05**, on one machine, against a stack built from the commit under test and
served at `http://localhost:8090`. **Four defects came out of it, and one of them meant the screen
every Site is born with could not be saved** — which no test had said, because no test had opened
one. The steps below are what was followed; where the walk found a step wrong, the step says so and
[The result](#the-result) names it. The headings, the commands and the claims are otherwise as they
were written before the walk.

**What this walk is not, and the distinction matters.** It was taken by driving the API and the
served client, not by a person looking at a rendered page. **Everything a machine can check was
checked; the judgements that need eyes — whether a Bad tile stands out, whether an unreadable one
reads as obvious rather than alarming, whether twelve columns are enough for a real screen — are
still unjudged**, and each step below says which of its watches were confirmed and which were not.
Phase 5.5's walk found ten defects the suite had passed, and the ones it found were the ones a
person noticed. This walk is the other half.

The record is deliberately in the same form as [`phase-5.5-manual-gate.md`](phase-5.5-manual-gate.md),
[`phase-6-manual-gate.md`](phase-6-manual-gate.md) and
[`phase-7-manual-gate.md`](phase-7-manual-gate.md): numbered steps with what to do, what to watch,
and a place to write down what it actually said.

**How the stack was built and run**, because a walk that cannot be repeated is an anecdote:
`deploy/build-images.sh` from the commit, then
`docker compose -f deploy/docker-compose.yml --env-file deploy/.env up -d` with the image tag set to
that commit and `SCADA_HTTP_PORT=8090`. **A fresh volume every time** (`down -v` first): the demo
seeder only seeds an empty database, so a stack reusing a previous walk's database gets no demo
configuration and step 2 has nothing to look at. Every image here is built from a commit and never
from the working directory, which is what makes "the commit under test" a meaningful phrase.


Phase 8's test gate is in [the phase plan](phase-plan.md):

> - A screen created through the API appears for a viewer on that Site and not for one who may not
>   see it, and a screen on an unseen Site answers 404.
> - A component bound to a tag renders that tag's live value, its quality and its source time; a Bad
>   value is marked and is not shown as a number.
> - A component bound to a tag the reader may not see renders as unreadable rather than as zero, an
>   empty box, or nothing at all.
> - A binding to a tag that no longer exists reads as missing rather than breaking the screen.
> - A new Site has a screen with the seed's components, and a viewer sees it.
> - The layout survives a round trip through the API unchanged, and an unknown component kind is
>   refused with a reason rather than stored.
> - The client's pure parts — what a component resolves to for a given snapshot — are unit-tested
>   without a browser, as the rest of the client is.

**Six of those seven are closed by tests that run**, and which one is which is written out in
[`open-work.md` §2.0b](open-work.md). The seventh is the sentence *"renders"*: a test can assert that
a resolver returns the right four-case answer for a snapshot, and **it cannot assert that a person
looking at the tile understands it.** That is what this walk is for.

**Why this one is expected to find things.** Phase 5.5's walk found **ten defects the suite had
passed**, most of them visible only on screen, and the rule the project took from it is that *a phase
with a screen is not done until somebody has used the screen*. Phase 8 is the screen on which every
other feature is finally judged, so the same surface is larger here, not smaller.

## 0. Before anything is started

**What has to be running.** The sequence is [phase-1-running.md](phase-1-running.md)'s, unchanged:
PostgreSQL, then the Migrator, then the Gateway, then either the built client served by the Gateway
or `ng serve` on `:4200` with its proxy. **The Migrator runs first** — since Phase 5 the Gateway does
not migrate and refuses to start on a schema mismatch (ADR-0012), naming both versions.

**Two Sites, and this is the point.** Everything about ADR-0004 and ADR-0011 that a screen can show
is a comparison, and a comparison needs two. Create a second Site before starting, with a device and
at least one tag on each. **A person who has walked only one Site has not walked this gate.**

**The seed is a claim worth checking by accident.** A new Site is seeded with one screen
(ADR-0024, scope item 5), so the second Site you create is also the check that the seed happened —
and the first screen a new user sees is not blank. Phase 6.5's walk recorded a blank first screen as
a defect; the seed exists so that it cannot happen again, and step 2 is where that is confirmed
rather than assumed.

**What to have open.** Two browser profiles, or one normal and one private window. Signing in as a
Viewer on Site A and as a Viewer on Site B in the same profile means signing out between them, and
a walk that spends its time signing in is a walk that skips the last three steps.

**The accounts to have ready**, from Phase 5's model (ADR-0011):

| Account | Needs | For |
| --- | --- | --- |
| Admin | tenant-wide | creating Sites, devices and tags |
| Operator on Site A | `CanOperate` on A | the editing steps |
| Viewer on Site A | `CanView` on A only | what a screen must read like without edit rights |
| Viewer on Site B | `CanView` on B only | the 404-not-403 comparison, and cross-Site |

## 1. A tag with a live value, and one that goes Bad

**Do.** On Site A, create a device and a tag through *Browse* — the same screens Phase 2 closed —
and point it at something that produces a real value. The Modbus simulator will do; so will an MQTT
source already publishing.

**Watch.** The value in *Browse* moves. **Note the tag's name and id**, because the screen steps
bind to it.

**Then take the source away** (stop the simulator, or unplug the device) and leave it away.

**Watch, and this is the step's whole point.** The tile in *Browse* reads **Bad**, and it does not
keep the last number as though it were still true. ADR-0003 is the reason, and a screen that shows a
stale number as a live one is the exact failure that ADR exists to prevent.

**Record:** whether the value was ever a number, how long Bad took to appear, and what the tile said.
**A device that accepts a connection and then says nothing is a different case** and takes longer —
the driver's own bound is five seconds now (see `open-work.md` on the NModbus finding), so if the
Bad is slow to arrive, say how slow. That number has been wrong by a factor of four once already.

## 2. A new Site already has a screen, and it reads

**Do.** *Screens* → select the **second** Site. Do not create a screen; look at what is there.

**Watch.** A screen named **Overview** exists, with the seed's components on it — a label and an
alarms component (ADR-0024). **Nobody made it.** That is the seed, and the thing to check is that it
is *useful* rather than merely present: a label that says nothing and an alarms box with no alarm to
show is technically a screen and is also the blank first screen with extra steps.

**Record:** what the seeded screen actually said, and whether it was worth looking at.

## 3. A bound component shows a value, its quality and its source time

**Do.** As the **Operator on Site A**, open *Screens*, turn editing on, and add a component of a kind
that reads a tag — a *value* tile — bound to the tag from step 1. Save.

**Watch, in this order.**

1. The tile shows the tag's **live value**, and it updates without a reload. The stream is
   `tag-stream.ts` over SignalR; a screen that needs a refresh to move is a defect, not a setting.
2. The tile shows the value's **quality** — and with the source still away from step 1, that means
   **Bad**, visibly, on the tile itself.
3. The tile shows the **source time**, and it is the time the *source* measured, not the time the
   browser drew it. For Modbus there is no device-side timestamp and the time of the successful read
   is used and documented as such (ADR-0003, `CLAUDE.md`); say which one you are looking at.

**Record:** the value, the quality, the source time, and whether all three were legible at once.
**"The value is there but I cannot tell whether it is current" is a defect**, and it is the one this
step exists to catch.

## 4. The four cases, which is where the honesty is

**Do.** With editing on, put four components on the screen:

1. one bound to a tag that **exists and is readable**;
2. one bound to a tag on **another Site**;
3. one bound to a tag that **used to exist** — create one, bind to it, then delete the tag through
   *Browse*;
4. one with **no tag at all** (a label, or an alarms component).

**Watch, and these must be four different things on screen.** The client's resolver has four cases
and the failure it exists to catch is collapsing any two of them (ADR-0024, scope item 2):

| Case | Must read as | Must **not** read as |
| --- | --- | --- |
| Readable | the value | — |
| A tag on another Site | **unreadable** | zero, an empty box, or **nothing at all** |
| A tag that has gone | **missing** | unreadable, or a break in the screen |
| No tag | whatever the kind shows | a value, or unreadable |

**The one to look hardest at is the third column's "nothing at all".** A hidden component makes a
screen look *complete while showing less* — which is why ADR-0024 chose to render the absence rather
than omit the row. **If a component vanished instead of saying it could not be read, stop and write
it down**, because that is the decision this whole slice was shaped around.

**Record:** what each of the four said, word for word if it was words.

## 5. A screen on a Site the caller cannot see is not found

**Do.** As the **Viewer on Site B**, try to reach Site A's screen directly — the id is in the URL, or
in the API response for Site A. Do it in the browser, and then do it again with `curl` or the
network panel, because the browser may be doing you a favour.

**Watch.** **404, not 403**, and not an empty 200. Every Site-scoped path in this project answers
this way and there is a test that fails if it changes (ADR-0011, `CLAUDE.md`): an id that exists
elsewhere must be indistinguishable from one that does not exist, or the path can be used to
enumerate what a caller may not know about.

**Then look at the list.** Site A's screen must not appear in Site B's *Screens* list at all, and
Site B's must not appear in A's.

**Record:** the status code, and what the API said versus what the screen said.

## 6. A save is immediate, and the author should have been told

**Do.** As the **Operator on Site A**, edit the screen and save. Then, **in the other browser
profile as the Viewer on Site A**, look at the same screen without reloading, then reload.

**Watch.** The change is there. There is no draft, no version and no publish step (ADR-0024) —
**Save changes what every operator on the Site sees, at once.**

**Then watch what the button said.** This is the step that is most likely to produce a defect,
because as of the slice that added the editor there is **nothing on the screen that says so** — it
is recorded in the phase plan as *"saying that a save is immediate"*, and it belongs on the button
rather than discovered by a colleague. **Write down what you expected the button to do and what it
did.** If you saved expecting a private draft and an operator saw it, that is the finding, and it is
worth more than a cosmetic one.

**Record:** what the Viewer saw, when, and what you believed you were doing when you pressed Save.

## 7. A write is visible and not yet actionable

**Do.** As the Viewer on Site A — not the Operator — look at a component bound to a tag that is
writable.

**Watch.** It is **marked as writable**, and there is **no way to write it from the screen.** Screens
are read-only in this phase (scope item 6); acting from a screen is the next slice, because it needs
the write path's permission story settled *on a screen* rather than in a form. The Gateway's
tag-write path exists and is Operator-gated since Phase 5, and ADR-0023 routes a write for an
edge-read device over the link — **neither of which is reachable from a screen yet, and that
absence is correct here.**

**Record:** whether "writable" was visible at all, and whether anything looked clickable that was not.

## The result

**Four defects, found in the order the steps met them. The first is the one that mattered**, and it
is the shape this project keeps meeting: a rule tightened, one place that wrote the same kind of data
did not follow, and nothing said so until somebody used the thing.

### 1. The screen every Site is born with could not be saved

`ScreenComponentKinds.TakesTitle` was answering two questions with one word — *may* this kind carry
text, and *must* it. It said yes to both for `label` and for `alarms`, and `ScreenRules` refused a
screen whose text was empty. So an `alarms` component was required to have a heading.

**ADR-0024 does not say that.** Its kinds table gives text as what a `label` shows — a heading, a
unit, an instruction — and says nothing of the sort for `alarms`, whose subject is a Site's standing
alarms. The code was ahead of the ADR.

And `SeedForSiteAsync` writes rows directly, so nothing had ever asked it for the heading. Every
Site's seeded screen contained an `alarms` component with no title, and **an author who opened the
screen their Site was born with and changed anything was answered
`A 'alarms' component shows text and needs some`.** The operator met it as a Save button that
refused to work, with the reason pointing at a component they had not touched.

*Found by:* step 3, which saves the seeded screen unchanged — the smallest thing an author does.
*Fixed in* `49a0ce6`: `NeedsTitle` is now the narrow question and `TakesTitle` the wide one; the seed
carries a heading; **and the seeder checks its own screen against `ScreenRules` before writing it**,
so this class of defect cannot come back.

### 2. A Site the seeder did not name would have been born empty

The seeder listed its Sites by hand — one `SeedForSiteAsync` call each for Skopje and for Bitola — so
**"a new Site is not born empty" (ADR-0024 §5) held only for as long as nobody added a third Site to
the statement above those calls.** A third Site would have been seeded by the database and given no
screen, and nothing would have said so: an operator would have found it, which is exactly what the
rule exists to prevent.

*Found by:* step 2, reading the rule instead of the data. Two Sites had screens and the code that
gave them to two Sites would not have given one to three.
*Fixed in* `651e0fc`: the Sites are read back from what the transaction wrote, so the screen seed
cannot disagree with the Site seed, and `SeedScreensAsync` is extracted so the rule is testable.

### 3. The heading was required, collected — and drawn for nobody

`resolveComponent` carried no heading for an `alarms` component and `screen-view.ts` never drew one.
So the text that was required by the API, that the editor collected, and that made the seed invalid
**was displayed to nobody.** An author had to invent a heading no reader could see.

*Found by:* fixing defect 1 and then asking where the heading went. It is the reason defect 1 felt
like a trap rather than a bug: the field existed only to satisfy a rule.
*Fixed in* `49a0ce6`: the heading travels through the resolver and the view draws it.

### 4. A writable tag was not marked writable

ADR-0024 §9 says *"a writable tag is marked writable on a screen; acting on it is not built here"*,
and the phase plan repeats it as scope item 6. **Nothing anywhere marked one.** A writable tag was
shown exactly as a read-only one.

*Found by:* step 7.
*Fixed in* `66a8741`: the server answers it beside `readable`, and it is a marker rather than a
button — a dashed pill with nothing to press, because writing from a screen is the next slice and a
marker that looked pressable would promise something this build cannot do.

### What each step found, and what it could not judge

| Step | Confirmed | Not judged — needs eyes |
| --- | --- | --- |
| 1 | a live value, its quality and the source time; the tag goes Bad and stops reading as a number | whether the Bad tile *stands out* |
| 2 | every Site has a screen; the seed then could not be saved (defects 1–3) | whether the seeded screen was worth looking at |
| 3 | a bound component saves and reads back; the seeded screen saves unchanged after the fix | whether the value, quality and time are legible at once |
| 4 | a cross-Site tag is refused at save; a deleted tag's binding survives as `readable: false` and resolves as *missing*, not as a break | whether "unreadable" reads as obvious without being alarming, and whether the row visibly stays |
| 5 | **404, not 403**, for both the screen and the list, as a Viewer on another Site | — |
| 6 | Save is immediate, and an author is told nothing (recorded, not fixed) | **what a person actually expects the button to do** — this is the step the walk could not take |
| 7 | a Viewer cannot edit (403); a writable tag was not marked (defect 4) | whether "writable" is noticeable without looking like a control |

**Two more things the walk could not close**, and they are recorded rather than glossed:

- **Every watch that needs a person is still open.** The table above marks them. A Bad tile that is
  legible in a JSON payload and invisible on a dark screen is a defect this walk could not see, and
  the honest statement is that **nobody has yet looked at a rendered page**.
- **Step 6's finding was left unfixed on purpose.** Nothing says the save is immediate. That is a
  sentence for the button, and the walk deliberately did not choose it, because the wording should
  come from how the surprise actually reads — which needs the person who was surprised.

**What is now true:** the storage, the API, the seeder, the renderer and the editor exist; four
defects the gate's own steps found are fixed with tests behind them (543 .NET, 103 client); and
**the part a machine can check has now been checked by running it, not only by reading it.** What
remains open is the part this file was written for, and it is smaller than it was.
