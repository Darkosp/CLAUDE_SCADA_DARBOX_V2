# Open work — what is unfinished, and what each item waits for

This is a register, not a source of truth. [`README.md`](../../README.md),
[`CLAUDE.md`](../../CLAUDE.md), the
[ADRs](../architecture/decisions/README.md) and
[`phase-plan.md`](phase-plan.md) remain binding; where this file and one of
them disagree, they win and this file is wrong.

It exists because the project has twice paid for the same mistake: a sentence
that stopped being true sent a later session looking for work that was already
done. Every item below names **what it waits for**, so that when the resource
arrives the work resumes without re-reading the whole history.

Started 2026-10-01. It was written after reading every document in the
repository against the code they describe; the correction that prompted it is
in §2.1.

## 1. Waits for hardware, or for a second machine

None of these can be finished on one machine, and none of them is a defect:
each is a claim the project makes that has only ever been tested under
conditions weaker than the claim.

**§1.1 and §1.2 were walked on two hosts on 2026-10-02** and are no longer
open. The record, with every number and the two things that looked wrong, is
[`phase-7-manual-gate.md`](phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02).
Each item below keeps its own text and carries what the walk changed. §1.3 is
untouched: both hosts that walk used were x64.

### 1.1 A link between two hosts

- **What has never happened.** Every outage walked so far was a Docker network
  disconnect on one machine: the edge container and the cloud stack on the same
  host. No walk has crossed a switch, a firewall or a NAT, and no TLS session
  has been carried over a real wire.
- **Why it matters.** A disconnect is an instantaneous, total, orderly loss. A
  real link fails slowly, partially and asymmetrically — a half-open socket, a
  NAT that drops the mapping, a switch that comes back with a different path.
  The broker session and the edge's reconnect loop are exactly the parts those
  differ on.
- **Needs:** a second machine (or a second host on a real network).
- **Detail:** [`phase-7-manual-gate.md`](phase-7-manual-gate.md) step 0 (the two
  decisions to make before starting), its *What was not measured*, and — added
  2026-10-02 —
  [*Appendix: the walk on two hosts, and two clocks*](phase-7-manual-gate.md#appendix-the-walk-on-two-hosts-and-two-clocks),
  which is the recipe: the broker's name and not its address (`certs.sh` writes
  DNS SANs, so the second machine needs a hosts-file entry), the port proved
  reachable from it first, both clocks right before the skew is staged, and the
  cut made by disabling that machine's network rather than a Docker disconnect.
- **When it exists:** walk that procedure on two hosts, and record it as the
  first walk's record does — the numbers, the cut and reconnect times, and
  everything that looked wrong or merely confusing.
- **Walked 2026-10-02, on two hosts, and closed.** The outage was a real network
  boundary: B's WiFi adapter disabled for 2 min 2 s, not a Docker disconnect.
  `tag_sample` held **119 readings measured inside the outage**, all 379 rows with
  379 distinct times, **0** of them invented for the window, stored as one batch
  at `17:03:01.860266`, the longest having waited **2 min 6.473 s**. The two ends
  saw it differently, which is the reason this item existed: the broker logged the
  client gone only at `17:01:14`, as `disconnected: exceeded timeout`, **20 s
  after the adapter was disabled**. The edge did not hang on the dead socket — it
  logged `Cannot reach the broker` once a second and made 76 reconnect attempts in
  122 s. The record is
  [`phase-7-manual-gate.md`](phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02).
  **What it did not do:** put the two hosts on separate networks behind a router
  or a NAT. Both were on one `/24`, so a NAT that drops a *mapping* — the
  appendix's fourth question — still has not been produced by a walk.

### 1.2 Two clocks

- **What has never happened.** Source timestamps have only ever come from one
  machine's clock, so no `SourceClockSkew` entry has ever been produced by a
  walk. The path exists and is tested (`PushedSources:ClockSkewTolerance`,
  30 seconds by default).
- **Why it matters.** ADR-0017's whole position on time is that the edge's
  clock is neither trusted nor overwritten. With one clock, the walk cannot
  tell "the edge's timestamp was carried across untouched" apart from "the
  store's own time happened to be the same".
- **Needs:** two machines whose clocks genuinely disagree.
- **When it exists:** set one clock past the tolerance, confirm the journal
  records the skew, and confirm the stored `source_time` is still the edge's.
  The two-host appendix above carries the trap: both clocks have to be right
  before the offset is staged, or the walk's own numbers mean nothing.
- **Walked 2026-10-02, on two hosts, and closed.** B's clock was put **+45 s**
  past the 30 s tolerance. The Gateway logged `the source's clock is 47 s ahead
  of the Gateway's; journalled. Its samples keep the times it gave them.` and
  `alarm_event` holds one `SourceClockSkew` row with `clock_skew_seconds =
  46.8458066`, `recorded_at` on A's clock (`17:10:07.975926`) and `source_time`
  on B's (`17:10:54.821733`). **136 samples were stored with B's times, 46.8 s
  ahead of the cloud host's own clock, `quality` Good** — the edge's clock
  neither trusted nor corrected, which is the whole claim. B's clock was put back
  and the live lag returned to the `−1.80 s` measured before the skew was staged.
  **The caveat the walk leaves behind:** neither host had a working NTP source
  (`Source: Local CMOS Clock`), so the pre-skew offset was observed rather than
  set, and the standing `ingested_at − source_time` of about −1.8 s is a clock
  offset plus pipeline delay that this walk does not separate. What it does
  establish is that the staged +45 s appeared in full, to within 5 ms.

### 1.3 A real arm64 board

- **What has happened.** The image and its publish are arm64, and the agent has
  run under QEMU on this x64 machine, with its readings reaching the cloud
  database (this repository's PR #4; numbers in the gate record).
- **Why it matters.** QEMU says the build and the image are right. It says
  nothing about `e_sqlite3` on the board, about performance, or about a plant's
  power and disk.
- **Needs:** the board itself — a Raspberry Pi or whatever the plant uses.
- **Detail:** [`0018-edge-agent-runtime-and-buffer.md`](../architecture/decisions/0018-edge-agent-runtime-and-buffer.md)
  and [`phase-7-manual-gate.md`](phase-7-manual-gate.md#since-the-walk-linux-arm64).
- **When it exists:** build on the board, run the agent, confirm readings reach
  the cloud, and record the board and the image digest.

## 2. Built, and walked on 2026-10-01

### 2.0b Phase 8's first slice, written 2026-10-03

**Screens are built and nothing has looked at one.** The storage, the API, the seeder and the
client renderer all exist with tests behind them; what no run has done is put a screen in front of
a person. This is the same split Phase 7's gate used between its terminal half and its screen half,
and it is recorded here for the same reason: the parts a machine can check are checked, and the part
that needs eyes has not happened.

**What the tests do cover** — six Core rules, five storage tests, eleven through the API, and twelve
on the client's resolver. Three things they pin that are worth knowing are pinned:

- **the composite keys**, at the level they are enforced: a component naming a tag on another Site is
  refused by the database, not by the API. Getting there took two keys rather than one, because a
  tag has no Site of its own — its device does — and the migration records why adding a `site_id` to
  `tag` was refused instead (it would mean recreating `tag_active`, and nothing reads a tag's Site);
- **`readable` is decided by the server**, so every renderer gives the same answer and there is one
  place to check. Mutating it to always-true fails the tag-has-gone test and the cross-Site test;
- **the resolver's four cases**, which is where the client's honesty lives: Bad, unreadable, absent,
  and no-tag-at-all are four different sentences and collapsing any two is the failure the client
  tests exist to catch.

**Two defects were found by writing the tests, and both are worth the record.** The migration granted
nothing, so every read worked and every write failed — since 0009 a new table gets SELECT and INSERT
only and has to ask for more, and the failure is invisible to a read path. And `UpdateAsync`
soft-deleted a screen's components before inserting the new set, which collides the moment an author
resends a component that kept its id; soft deletion exists so history can resolve a *name* (ADR-0009)
and a component has no name, so it is a DELETE and an INSERT.

**What has not happened:**

- **No person has looked at a picture of a screen.** The walk was taken on 2026-10-05 — see
  [`phase-8-manual-gate.md`](phase-8-manual-gate.md) — but by driving the API and the served client,
  not by looking at a rendered page. **Four defects came out of it**, so running the thing rather
  than reading it was worth doing; **every watch that needs eyes is still open.** Whether a Bad tile
  stands out, whether an unreadable one reads as obvious rather than alarming, whether twelve columns
  are enough for a real screen, whether the new "writable" marker is noticeable without looking like a
  control — all unjudged. Phase 5.5's walk found ten defects the suite had passed and the ones it
  found were the ones a person noticed; this walk is the other half of that.
- **No screen has been through a browser against a live Gateway, by a person.** What has now happened
  is that the *client the Gateway serves* was fetched and checked to contain the build under test,
  and every API path the client calls was exercised against the running stack. The Javascript was
  never executed in a browser.
- **The editor has no drag and drop**, and does not pretend to: components are added by a picker,
  sized by a pair of buttons and moved by arrows. That is enough to build a screen and it is not what
  an author would choose twice; a builder is the next thing this wants, and it is a client feature
  over the same rows rather than a new model.
- **Nothing warns that editing a screen affects every reader at once.** A screen has no draft version
  and no publish step (ADR-0024 takes no position on one), so an author pressing Save changes what
  every operator on that Site sees, immediately. That is defensible for a screen and it is the kind
  of thing that should be said on the button rather than discovered. **Step 6 of the walk confirmed
  the author is told nothing and deliberately did not choose the wording**, because the sentence
  should come from how the surprise reads — and the walk could not be surprised.

**Closed on 2026-10-05: the live preview.** An author editing a screen now sees what an operator will
see, drawn by the operator's own `app-screen` component given the draft instead of the saved screen —
so there is no second renderer that can drift from the first, and a fix to one is a fix to both.

**What the preview does and does not show, and why the difference is honest.** Values, qualities,
source times, labels, status and alarms are live, because the preview is given the session's own
snapshot map and alarm list. A **trend** in the preview says "Reading…" — history is keyed by component
id and a component an author has just added has an id the server has never seen, so there is nothing to
fetch it under. That is the same sentence the read view shows for a screen whose history has not
arrived; the editor's own note on its `history` input records the real fix (key history by *tag*, not
by component) and why it is its own slice.

**The one thing the preview depends on, and the mutation that proves it is tested.** `newComponent`
sets `readable: true`, because `readable` is the server's answer about the *reader* and a component
that has never been to the server has no answer yet. If that ever became `false` every component in the
preview would read "Not available to you" — an author looking at a screen that says the opposite of
what the operator will see. **Mutated to `false`: two named tests fail in
`src/Web/tests/screen-preview.test.mjs`** while the control that sets the flag explicitly to `false`
and expects `unreadable` stays green. It is the one place `writable` behaves differently: a component
being authored reports `writable: false`, because unlike readability there is nothing to be optimistic
about — it has no tag the server has looked at, and the preview is the place an author compares against
what an operator sees.

### 2.0d Phase 8's four walk defects — found and closed on 2026-10-05

**The walk in [`phase-8-manual-gate.md`](phase-8-manual-gate.md) found four defects, and the first one
meant the screen every Site is born with could not be saved.** They are listed in full there with what
each was and how it was fixed; this is the summary and the two things worth carrying forward.

**1. The seeded screen was unsaveable.** `TakesTitle` was answering "may this kind carry text" and
"must it" with one word, and answered yes to both for `alarms` — while ADR-0024's kinds table gives
text as what a `label` shows and says nothing of the sort for `alarms`. The seeder writes rows
directly and had never supplied a heading, so every Site's screen contained an `alarms` component that
the API would refuse. An author opened it, changed anything, and got `A 'alarms' component shows text
and needs some` — pointing at a component they had not touched. Fixed in `49a0ce6`, with `NeedsTitle`
split out and **the seeder now checking its own screen against `ScreenRules` before writing it**, so
the shape cannot come back.

**2. A Site the seeder did not name would have been born empty.** The seeder listed its Sites by hand,
so "a new Site is not born empty" (ADR-0024 §5) held only while nobody added a third Site. Fixed in
`651e0fc`: the Sites are read back from what the transaction wrote.

**3. The heading was required, collected, and drawn for nobody.** `resolveComponent` dropped it and
`screen-view.ts` never drew one, so the field existed only to satisfy a rule. Fixed in `49a0ce6`.

**4. A writable tag was not marked writable**, which ADR-0024 §9 requires and nothing did. Fixed in
`66a8741`: answered by the server beside `readable`, drawn as a marker and deliberately not a button.

**The lesson, and it is the same one Phase 5.5 taught.** *A rule and every place that writes the same
kind of data have to move together.* Defects 1 and 2 are both that shape — a validation that tightened
and a seeder that did not follow — and neither was visible from reading the code, because the code
each half was in was correct on its own. What found them was **running the smallest thing an author
does: opening the screen their Site was born with and saving it unchanged.**

**A wrong test was written on the way, and that is worth recording too.** The first attempt at pinning
defect 1 asserted the seeded screen passes `ScreenRules` — and it passed with the title removed again,
because the rule had been relaxed in the same change. A test that cannot fail is not evidence. It was
replaced with one that asserts what the rules cannot: every text field on the seeded screen is filled
in, which fails with `the seeded 'alarms' component carries no text` when the heading is dropped.

### 2.0c The demo seeder's race — found, and closed on 2026-10-05

**`DemoConfigurationSeeder.SeedIfEmptyAsync` could seed twice, and no longer can.** It asked
`SELECT count(*) FROM tenant` on one connection and then seeded on another, which is a read followed
by a write with nothing between them: two processes starting at once against an empty database both
read zero and both inserted the whole demo dataset — two tenants, two Skopjes, two of every device.

It was found because `GatewayTestHost` starts the app once per test with the tests running in
parallel against one database, which is a much better double-start generator than a deployment is.
That is also why it was recorded as a sighting rather than a production defect at the time: no
deployment in this project has ever started two Gateways against an empty database. What makes it
worth having closed anyway is the shape — a rolling start, or a Kubernetes `Recreate` written as
`RollingUpdate` — and that the same race duplicates the *Site*, which every other entity hangs off.

**The fix is a transaction-scoped advisory lock taken before the question is asked**
(`pg_advisory_xact_lock`, key `0x5343_4144_5F53_4545`), with the probe and the insert inside that one
transaction. The second seeder waits and then asks its question *after* the first has committed, so
the answer is true rather than merely earlier. Transaction-scoped rather than session-scoped for the
reason `MigrationLock` gives: a process that dies mid-seed releases it without anyone cleaning up.

**Proved by mutation.** With the lock removed, `DemoSeederConcurrencyTests` fails with
`23505: duplicate key value violates unique constraint "tenant_pkey"` — which is what two seeders
colliding on fixed ids looks like, and is also why the test asserts that nobody threw rather than
only that the rows came out once. A later ordinary run is asserted in the same test, because
idempotence on an already-seeded database is what an upgrade depends on and xUnit promises no order
between two test methods sharing one fixture.

### 2.0e Phase 5.5's two deferred items — closed on 2026-10-05

**Both were raised by walking Phase 5.5's gate by hand, both were deliberately left out of that
phase, and both are now built.** They are recorded here with what each has **not** had, which is the
same thing every other section here records.

**Flapping — closed by [ADR-0025](../architecture/decisions/0025-an-alarm-waits-before-it-announces-itself.md).**
The walk recorded the shape: one Site wrote three journal rows every ~25 seconds while a value
oscillated across a limit. An alarm definition now carries an **on-delay** and a **deadband**, and the
two are separate settings because they are separate decisions — a wait trades speed for quiet, and a
band trades the position of a limit for quiet. Both nullable, and **null is not zero**: the migration
cannot change what an existing alarm means, which is the most important property it has.

**What the tests pin**, and each of these is a fact about the engine rather than about a screen: a
breach that stops inside the delay raises nothing **and leaves the journal unchanged**, against a
control with no delay that raises immediately; the raise carries the reading that *confirmed* the
breach and records the wait it waited; a value crossing to the other limit starts its wait again; a
deadband does not delay the raise and holds the alarm until the value is past the limit by the band,
strictly, in both directions; and a restart resets a wait in progress without writing anything about
it.

**One decision the ADR got wrong, found by writing its test — and this is the part worth reading.**
The first draft said a Bad reading "neither advances the wait nor cancels it". The test failed, and the
failure was right: **pausing a wait has the Gateway counting time in which it was not watching.** A
device offline for ten minutes would satisfy a sixty-second delay while nothing at all was measured,
so the alarm would raise on the strength of an outage. The wait is now abandoned and starts again from
the first Good reading. The ADR carries the correction rather than the original claim.

**What has not happened:** no run has configured a delay or a band through the browser, and **no alarm
has been watched waiting**. Everything above is the engine driven by a stub clock. The threshold form
gained two fields and **nobody has typed in them** — which is the same gap §2.0b records for the
screens, and the same reason: it needs a person.

**Filtering the journal — closed 2026-10-05.** `/api/alarms/journal` takes repeatable `tag` and `type`
parameters, the client sends them, and the filters are ANDed with the Site filter and with each other.

**What the tests pin:** that narrowing by tag returns that tag's rows and not another's, against a
control that sees both unfiltered; that a Site filter still lets the engine's own rows through **when
nothing else is narrowing**, which is ADR-0013's rule and the thing this feature could most easily have
broken; that two filters are intersected rather than treated as alternatives; and, on the client, that
an absent filter is left out of the request entirely rather than sent empty — because `tag=` matching
nothing would show a reader an empty journal and let them conclude the plant had been quiet.

**One choice made against the first instinct.** The engine's own rows are *removed* by a tag or type
filter, though they survive the Site filter. The argument is that a Site filter is enforcement — a
reader does not pick it and cannot see past it — while a tag filter is something the reader chose, so
it means "these rows only", and a filter that quietly returned rows failing one of its two conditions
would be one a reader could not reason about. The first version of the test asserted the opposite and
was rewritten; `AlarmJournalQuery` records both readings and why one won.

**What has not happened:** nobody has used the filter pickers. The client suites cover the query string
they build, and **no person has narrowed a journal and read the result** — so whether "Nothing matches
these filters. The plant may still have been busy." is the right sentence, and whether two dropdowns
are the right control, are unjudged.

### 2.0f The Modbus response bound — closed on 2026-10-05

The question this register carried was *driver constant or per-device setting?* **Per device, with a
five-second default.** `ModbusTcpDriverFactory.ResponseTimeoutFor` reads `responseTimeoutSeconds` from
the device's own connection settings, and the driver takes the result as a constructor argument.

**Why per device and not one number for the deployment:** how long a device takes to answer is a
property of the device and the link to it, exactly as `host` and `scanIntervalMs` are — not of the
Gateway. A plant with one device on a slow radio link and twenty on a local switch has one device that
needs a longer bound and nineteen that would be made slower to report a fault if they all shared it. A
deployment-wide setting forces that choice on every device at once.

**Why it is refused rather than clamped**, which is the decision worth recording: a device configured
for a thirty-second bound that silently got five would read Bad on a link that was merely slow, and
**nothing anywhere would say why** — the operator sees a device that does not work with no indication
that its own configuration was discarded. An unusable value throws, naming the setting, the value, the
device and the range. Blank is the exception, because that is how a form field left empty arrives and
it means "not set".

Bounded at 1 s to 120 s, because both ends are a way to build a driver that does not work: under a
second and a healthy device reads Bad for reasons that are the Gateway's fault, and over two minutes
and a scan loop effectively stops reporting faults at all — the silence ADR-0003 refuses, which is the
failure the bound exists to prevent.

**The test measures the bound rather than asserting a constant, and the mutation was watched.**
`A_request_that_is_never_answered_is_given_up_on_within_the_configured_bound` sets a two-second bound
against a socket that accepts and never answers, and requires the read to give up inside six. The
mutation: set `Retries` back to 3 — **measured 8.8 s, and it fails.** The numbers are chosen against
that failure and not for tidiness: with a one-second bound and a five-second ceiling, the same
regression passes, which is the shape of test this project has already been caught by.

**What has not happened:** no live device has been given a bound, and no operator has typed the setting
in. It **is** reachable from the browser — the device form's connection settings are a free key/value
list, deliberately ("named settings rather than fixed fields: Modbus wants host/port/unitId"), so
`responseTimeoutSeconds` can be added the same way `host` is. What is missing is not a field but
**anything that tells an author the setting exists**: the form does not list a driver's known settings,
so this one is discoverable only from the documentation. That is a smaller gap than a missing control
and still a real one, and it is the same class as the trend preview's "Reading…" — the work exists and
nothing on screen points at it.

### 2.0g Two things Phase 8's walk left open — both closed on 2026-10-05

**1. The preview's trend said "Reading…" instead of drawing a series.** The recorded cause was that
history was keyed by **component id**, and a component an author has just added has an id the server
has never seen — so the preview could ask for nothing. History is now keyed by **tag id**.

Keying by tag is the more honest answer as well as the fix: **the history of a trend is the history of
its tag**, and two trend components bound to one tag are two views of one series, so one fetch serves
both. The read view and the preview now call one function, `trendTagIds`, rather than each holding a
filter that looks like the other — which is the shape of defect this walk found twice, and the reason
the rule is a function.

**What the tests pin**, seven of them: that a readable trend asks for its tag; that two trends on one
tag ask once; that a `value` component asks for nothing however it is bound; that an **unreadable**
trend asks for nothing — the exclusion that is about the sentence on screen, because an unreadable
trend renders as unreadable (ADR-0024 §5) and a fetch that could never succeed would leave it on
"Reading…" claiming an answer is coming; that a trend bound to nothing asks for nothing; and that the
exclusions combine rather than cancelling each other.

**What has not happened:** no author has watched a trend draw in the preview. The mechanism is tested
from the client's own data shapes, and **the fetch path against a live Gateway with a draft's tags is
unexercised** — as is every judgement about whether a 15-minute window is the right one for an author
to judge a trend by.

**2. Nothing said that a save is immediate.** `phase-plan.md` asked for it *on the button*; it is a
sentence **under** the buttons, because a button label that is a paragraph is not a button label. It
says that saving replaces the screen every operator on this Site sees, immediately, that there is no
separate publish step, and that Cancel will not undo it afterwards.

**This is a candidate, not a conclusion, and that is deliberate.** Step 6 of the walk exists to find
out what an author *expects* the button to do, and the wording was left unwritten so that the answer
would come from someone who had been surprised. What changed the decision is that **leaving it
unwritten does not make that answer better** — it leaves the next author to be surprised by the same
thing. So the step now reads the sentence and is asked whether it is in the right place and whether it
is enough. **Nobody has read it yet.**

### 2.0h Operating from a screen — closed on 2026-10-05

ADR-0024 §8 said a writable tag is marked writable and acting on it "is the next slice's". This is that
slice, and it is closed by **[ADR-0026](../architecture/decisions/0026-operating-from-a-screen.md)**.
The ADR was written first, as the register required, because **this is the second thing in the project
that lets a web page change a plant** and the first whose control sits beside the value it acts on.

**What was built, and what it deliberately did not touch.** One `value` component offers a `Write…`
control; the server's `writable` flag decides whether it is offered, exactly as it already decided
`readable`; the dialog shows the tag's path, its reading, its quality and its source time; `Enter`
submits because it is a form, and a boolean tag gets a two-option picker rather than a text box. **The
write path itself was not changed at all** — no endpoint, no permission rule, no new way to reach a
device. If any of those had been needed it would have meant ADR-0011 or ADR-0023 was wrong.

**Two decisions were argued and reversed while writing it, and both reversals are in the ADR.** The
first draft added a `ScreenWrites:Enabled` switch, off by default; it was dropped because a switch
whose off position only hides one of two ways to reach the same capability invites a deployment to
believe it has disabled something it has not, and because ADR-0023's neighbouring switch defaults to
**on** with the words "an Operator may write one and always could". The first draft also added a
`tag.write_from_screen` audit action; reading the code refused it, because **the Browse view already
has a write form**, so API writes were already coming from people at screens — the available
distinction was which page they were looking at, not what kind of event it was.

**What the tests pin.** On the client: that a `value` component carries the tag a write is addressed to
and the kind of value the tag takes — both new on the resolved state, and both failing quietly if lost;
that whether a write may be offered comes from the component rather than from the tag's kind; and that
an unreadable component, one with no reading yet, and every other kind resolve with **no write target at
all**, which is what makes "one component writes and nothing else does" structural rather than a rule a
template has to remember. On the Gateway, the existing `writable` test already covers the three-way rule
the control hangs off, and its comment now says so.

**What has not happened, and it is the same gap as everything else in Phase 8: nobody has pressed the
button.** No write has been made from a rendered screen; the dialog has never been opened by a person;
whether `Write…` beside a reading is noticeable without looking like part of the value, and whether the
sentence about the current reading being the *last* one rather than the live one is clear, are
unjudged. **The write itself is well covered** — ADR-0023's path is tested from the cloud's side to a
real Modbus slave — but the control that reaches it is not, because reaching it needs an eye.

One thing worth carrying: the Browse view's write message said *"Sent X to Y"*, which claimed more than
the write path knows. It now says the write was **accepted**, and that the reading changes when the next
scan reports what the device holds — which is the true statement whether the device is polled here or
behind an edge.

### 2.0i The client's visual design — modernised on 2026-10-06

**A decision, not a defect.** The client had grown a look rather than chosen one. Before this, it held
**45 distinct hex values across five files**, including near-duplicates that had drifted apart
(`#1c6b3a` beside `#1d6b39`, `#99201f` beside `#96261f`) — Phase 8's lesson in its visual form, *a rule
and every place that applies it have to move together*, and a colour is a rule.

**What was decided.** A light background, and the "soft and neutral" direction out of three proposals
built and shown side by side: soft shadows instead of borders, cool grey surfaces, a near-black accent,
eleven-pixel corners. **The accent is deliberately without hue.** The only strong colours in this
product are the status ones, because here a colour has to *mean* something — Good, Uncertain or Stale,
Bad, no reading (ADR-0003) — and an accent that competed with them would make "green" mean two things
at once.

**What was built.** `src/styles.css` holds every colour, radius, shadow, type size and spacing as a
token, and **no other file in the client writes a raw colour** — checked by search, not asserted. The
app shell, the five component kinds, the journal, the tree, the editor, the write dialog and the trend
chart all draw from it.

**Two defects the browser walk found, neither of which a test could have.** The screens were generated
by driving the running application, signed in, with a headless browser
(`tools/screenshot-live.mjs`), and then looking at the result:

1. **The journal's table had no rules of its own.** Its column headings sat centred over left-aligned
   data and its timestamps wrapped onto two lines, because those are the browser's defaults for `<th>`
   and for a table with no width — not a decision anyone had made. It had been that way since Phase 5.5
   and nothing could see it.
2. **The screens view and the journal were bare text on the page background.** Both are `<main>`
   elements, and the stylesheet's card rule matched only `aside, section` — so the two views an
   operator spends all their time in were the only two without a surface.

**Two more things were corrected by looking again**, which is the argument for looking twice: wrapping
the screens view in a card put a shadow inside a shadow (a screen is already made of cells that carry
their own), so the frame came off and the screen is now the page's content rather than a panel on it;
and a pane heading with three children spread its controls apart, leaving Edit stranded in the middle
of the page, so they are grouped to the right instead.

**What has not happened:** no operator has used any of this, and **nobody has judged it at the size a
plant screen is** — whether the value is large enough to read from a step back, whether the Bad pill
stands out at a glance, and whether the near-black accent reads as deliberate rather than as an
unstyled button are all unjudged. They are the same class of question `phase-8-manual-gate.md` lists,
and they need the same thing: eyes.

**What this deliberately is not:** a dark theme. The tokens make one cheap to add later — a second set
of values, not a rewrite of the components — and **that is the whole reason the tokens exist**, so a
deployment that wants one is not asking for the client to be restyled a second time.

### 2.0j A symbol, the dark theme, and what looking at 1920 found — 2026-10-06

**Three things were asked for in one sitting and two of them are finished.**

**1. The screens view at 1920×1080, which is the size a control-room panel actually is.** Every view
was captured from the **running client, signed in**, by a headless browser (`tools/screenshot-live.mjs`,
now defaulting to 1920) and looked at. Three defects nothing had seen, and all three are the same kind
— a decision that was right at 1440 and wrong at 1920:

- tiles were captioned with the **full display path**, so every one wrapped or was cut. A tile now
  carries the tag's own name and the path in its tooltip;
- every tile printed its **source timestamp**, four lines of chrome to say four readings were fresh.
  The time now appears only when a reading is *not* fresh, so its presence is the signal;
- the reading was a **fixed 1.7rem** — on a 1920 panel a number read from two metres away. It scales
  with the viewport now, and a zero is dimmed, because a stopped flow is the reading most easily missed.

**2. A dark theme, which the token layer made almost free.** `[data-theme='dark']` is a second set of
values and nothing else — **adding it touched no component**, which is the whole reason the token layer
exists. Applied before the first paint by a small inline script in `index.html`, so there is no flash;
the choice is per browser in `localStorage`, beside the session controls rather than among the views.

It is not an inversion, and the differences are recorded in the file: surfaces get **lighter** as they
come forward, as in the light theme; the status inks are the light theme's backgrounds mixed much
darker, because a pale pill on a dark surface is the loudest thing on screen and a Bad alarm would
shout for the wrong reason; and the accent becomes near-white, which is the payoff of having chosen it
without a hue.

**3. A contrast audit found real defects in both themes, and this is the part worth reading.**

- the light theme's faintest text measured **2.82:1** on the page background, where AA asks for 4.5.
  `--text-faint` was doing the job `--text-muted` already did, badly, so it was **removed rather than
  darkened** — a near-duplicate that fails a contrast check is a duplicate;
- the light "no reading" pill measured 4.1:1. Fixed;
- the dark theme's faint text was 4.15:1. Fixed.

Locked by `src/Web/tests/theme-contrast.test.mjs`, which checks both palettes: three text levels
against three surfaces, every status pill against its own background, and that the quality colours can
be told apart. **Its first version was wrong and that is in the test's own comments** — it compared
luminance, and light's Stale and Bad are a brown and a red that are obviously different and have nearly
the same brightness. Comparing brightness would have demanded one of them be made paler for no reason a
reader would thank anyone for, so distance is measured in a crude perceptual space instead.

**4. ADR-0027 — a symbol derives a named state from its tag.** The first time ADR-0024's "the answer is
a new ADR adding a kind" has been acted on; there are six kinds now and the sixth is a picture of
equipment. The interesting decisions are in the ADR: the mapping is an **ordered list of declarative
comparisons rather than an expression**, because an expression is a language and a language needs a
parser, a debugger and a story about what an operator may type; **quality overrides the state**, so a
boolean tag that has gone Bad does not draw a turning pump, because a turning pump is a claim about the
world; and **a continuing value never drives an animation**.

**What is built:** the model, migration 0019 (a `jsonb` mapping with a CHECK that it is an array, and
the read view recreated to carry it), persistence, the API and its refusals, **and the client** — the
resolver, the pump drawing with its six states, and the editor's mapping form.

**The client half was finished the same day, and looking at it found two things.** The pump renders in
both themes and in both states — green and turning when its tag reads true, grey and still when it
reads false — and the dark theme needed no change to the drawing at all, because it draws from the
equipment and status tokens like everything else. What the live check found was **a defect in the
seed data rather than in the code**: the fallback rule was written with a comparison attached, and the
server refused it with *"The fallback state of a 'symbol' component (rule 2) cannot also have a
comparison."* That is the refusal working exactly as ADR-0027 describes, and it was right — a rule
that matches anything cannot also state what it matches. Both the seed and `newComponent` now write a
fallback with no comparison.

**Still not walked, and the ADR's own "Verified in review by" list is now true rather than half true:**
an author has not built a symbol from an empty screen through the browser, and **nobody has looked at a
turning pump on a plant screen** — whether the rotation reads at a glance, whether `stopped` and `bad`
are distinguishable without reading the word under them, and whether the mapping form is usable by
somebody who has not read ADR-0027 are all unjudged. They need the same thing everything else in Phase
8 needs: eyes.

**One more thing worth knowing before touching these queries.** The same Dapper trap bit **twice in one
sitting**: a row read by two queries whose column lists drifted, failing at run time with a message
about a constructor signature. The component column list is now named once
(`ScreenComponentColumns`), with a note at the definition saying what happened and the one case it does
not cover.

**And one piece of history was rewritten**, on this repository's own rule that history is not rewritten
on purpose. The design commit `cee86ba` was already pushed and a squash folded it into `c800d60`, which
needs a force-push to publish. It was done because `git diff cee86ba c800d60` shows **only additions** —
nothing was lost, which was checked before the push rather than assumed — and because leaving the two
commits halves the same change. Recorded here because the rule exists to stop silent rewrites, and a
recorded one is not silent.

### 2.0l The three things this session left open — 2026-10-06

All three were found by looking at the running application, and all three are recorded rather than
fixed because the session ended. Each says what it would take.

**1. A new symbol assumes its tag is boolean, and says nothing when it is not.**

`newComponent('symbol', …)` is born with `running when value is true`, and `stopped otherwise`. That is
right for the pump this slice shipped, because a pump's run signal is a boolean. It is **wrong for every
other tag an author might pick** — and picking one produces no warning, no refusal and no visible
symptom: a numeric tag simply never compares equal to `true`, so the symbol falls through to the
fallback and draws `stopped` forever, through every value the tag ever reports.

Found by watching an author pick a **setpoint** for a pump. The application was correct — a setpoint is
a target, not a running state, and ADR-0027 refuses to invent a state it was not told how to derive —
but nothing told them why. **A fallback that is silently always taken is the dangerous shape here**: it
looks like a working symbol reporting a stopped machine.

What it would take: either a default that follows the tag's `valueKind`, or a warning in the editor when
a mapping's comparisons cannot match the bound tag's kind — and probably both. It is a rules question
rather than a rendering one, so it belongs with `ScreenRules` on the server and in the editor's own
validation, not in the symbol.

**2. Nothing marks a value that cannot be true.**

`Tank 3 Level` read **`464.00 %`** — impossible for a level — and it was drawn exactly like
`4.79 bar`. That particular reading was this session's own demo data: the tag was seeded against a raw
register with no scaling, so the number is the session's fault rather than the product's. **The tag now
carries `scale=0.1` and reads `34.2 %`**, so the demo no longer shows it. **The question it raises is
not fixed and is not the demo's.**

A percentage above 100, a negative pressure on a gauge that cannot go below zero, a level that exceeds
its tank: none of these have anywhere to be said. ADR-0005 gives a tag a dimension and an SI factor,
which is a statement about **what a number means and how to convert it** — not about **whether a
particular value is possible**. So this is genuinely unanswered by the units work, and it needs a
decision before code: whether the product should have an opinion at all, and if so whether it belongs to
the tag's definition (a range), to the alarm engine (a limit), or to the screen. The safe default is
that the product says nothing it was not told.

**3. The `discrete` kind still has no editor, which is older than this session and easy to forget.**

Noticed while reading `writableAsANumberOrAFlag`: the write dialog accepts numeric and boolean and
refuses the rest, which is right. But `discrete` tags exist in the model and are readable, and there is
no way to author or write one from the browser. Recorded here so it is not rediscovered as new.

### 2.0k What the walk found on 2026-10-06, and one defect in the walk itself

**The walk is being taken, and it found a defect on its own second step.** Recorded separately from
§2.0j because this was found by a person using the application, not by anything written in this
session — which is the whole reason walks exist here.

**The defect: a tag could be added and deleted from the browser and never changed.**

The walk's step 4 asks the reader to make a tag writable, because the write control only appears on a
writable tag and the demo's `Pump Running` is not one. The instruction did not work, and **it could
not**: there was no `Edit tag` control at all. The Browse view offered `Edit device`, `Add tag` and
`Delete tag` — everything except changing a tag that already existed.

Everything needed was already present and unreachable. The API has
`PUT /api/devices/{deviceId}/tags/{tagId}`. The client has `saveTag(deviceId, tagId, …)`, which has
always taken a tag id. What did not exist was anything that **opened an existing tag into the draft**,
so `saveTag` was only ever called with `null` and every tag save was a create. The two halves were each
correct and the path between them was missing — the same shape as the four defects Phase 8's walk
found, and it is worth noting that **this one was found faster than any of them, by trying to follow an
instruction rather than by reading code.**

Fixed: `TagDraft` carries the tag's id (null meaning "being added"), `editTag()` fills the form from
the selected tag, and `saveTag` passes that id instead of a hard-coded `null`. Verified against the
running stack by driving the real browser (`tools/check-tag-edit.mjs`), which does not assert that the
button exists but **reads the HTTP method the page actually sends**: the form arrives filled from the
tag, and saving sends `PUT /api/devices/{deviceId}/tags/{tagId}`.

**The walk also found a defect in itself, and that is the more useful lesson.** Its step 4 told the
reader to check a box on a form that did not exist. A walk whose instructions cannot be carried out is
a walk that will be abandoned, and the person following it has no way to tell a missing control from
their own mistake — which is exactly what happened: the question asked was *"where do I type `false`"*,
and then *"there is no edit tag"*, and both were reasonable readings of a document that was wrong.

**Two more things the walk produced, neither a defect:**

- **The pump animates, and it is worth recording that this could only be learned from a person.** The
  pump runs and says `RUNNING`, and after a while it stops by itself and says `STOPPED` — the simulator
  cycles the tag. **A screenshot cannot show rotation**, so the single most important thing about the
  symbol was unverifiable by every instrument in this repository and was settled in one sentence by
  someone looking at it. It also proved more than a manual write would have: the animation followed a
  **real change from the plant**, not a value typed into a form.
- **`Tank 3 Level` read `464.00 %`**, which is impossible. That is this session's demo data rather
  than a product defect — the tag was seeded without scaling and the simulator writes raw counts into
  that register — but it raises a real question the walk should keep asking: **nothing marks a value
  that cannot be true.** `464 %` is drawn exactly like `4.79 bar`. Whether the product should have an
  opinion about a percentage above 100 is a design question, and it is not answered by the units work
  in ADR-0005, which is about dimension and factor rather than plausibility.

### 2.0 Written, and not yet walked

Decided, implemented, tested — and waiting only for a run that exercises it. These are not
defects and not open questions: the work exists and nothing has been through it end to end.

- **[ADR-0020](../architecture/decisions/0020-devices-with-no-tags.md) — a tagless device is
  omitted from the derivation.** Decided and implemented 2026-10-02 from §2.1's finding, with its
  own tests. **What has not happened:** no run has had a tagless device assigned to an edge while
  the new derivation was running, so the path the finding describes — assignment accepted,
  derivation omits it, the log line is written, and the first tag's save republishes it to the
  edge — has been tested in unit tests and not watched on a link. The two-host walk is the
  cheapest place to watch it: assign a second device to that edge with no tags, confirm the
  edge's configuration is still accepted whole, then add one tag and confirm the edge picks the
  device up without a second edit.
- **[ADR-0021](../architecture/decisions/0021-edge-reports-what-it-cannot-read.md) — an edge says
  which assigned devices it cannot read, on its own declaration (version 2).** Decided and
  implemented 2026-10-02, with its own tests and the client showing the result. **What has not
  happened:** no run has had an edge lose a driver under a live assignment. The state it is about
  needs a real edge whose build lacks a driver that one of its assigned devices uses — a stripped
  or rolled-back agent image is the cheapest way — after which the declaration should carry the
  device, the audit trail should name it, `/api/edges` should report it, the edge's panel should
  show it, and the assignment should be unchanged. **And the version half wants a walk of its
  own:** this is the project's first payload version bump, so the claim that a version 1 message
  still reads and a version 3 is refused whole has been tested against this build and never
  against two builds of different ages on a real link. **Also not walked:** that the declaration is
  republished when the set changes and not otherwise — the unit tests watch it through a real
  in-process broker, and no walk has watched it over the TLS link.
- **[ADR-0022](../architecture/decisions/0022-derived-link-device.md) — an edge's link device is
  derived, is not overridable, and the edge names its limits.** Decided and implemented
  2026-10-02, with its own tests. **What has not happened:** no run has created an edge against a
  real deployment and watched the derived link device connect. The unit tests cover the derivation
  — the topic, the settings, one device per edge, a second pass writing nothing, an existing link
  kept — and the Gateway's integration tests cover the API creating an edge with its link already
  in place. What no test does is watch the **derived** link actually subscribe and receive, because
  that needs a broker and an edge. The two-host walk is the place: create a new edge there and
  confirm the Gateway is subscribed to `{prefix}/{edge}/samples` before the edge is started.
  **And one thing an upgrade exercises that nothing here does:** an existing deployment's
  hand-made link devices are reused rather than replaced, which is what keeps the broker's session
  — and the queue under it — from being dropped by the change.
- **[ADR-0023](../architecture/decisions/0023-routing-writes-to-an-edge.md) — a tag write is routed
  to the edge that reads the device, and is never queued or retained.** Decided and implemented
  2026-10-03, with its own tests and four mutations recorded. **What has not happened: a write that
  travels the whole way.** Every joint is now tested and no run has crossed all of them at once — a
  browser asking the API, the router publishing, a real edge taking it off a real broker, a real
  device changing, and the result coming back to the operator's screen. That is the walk this owes,
  and it is the one to do first of the four here, because it is the only one where being wrong means
  a plant was changed or an operator was told it was.

  **What the tests now do cover**, so this entry is not read as "nothing is tested":
  - the cloud's half — the router matching a result to the call waiting for it, the API answering
    504 when nothing answers, the journal keeping `written` / `failed` / `unconfirmed` apart;
  - the wire format — a request and a result round-tripping, and every unreadable one refused whole;
  - **the edge's half, against a real Modbus slave on a real port** — a write reaching the right
    register through the configuration's address and the read path's own scale, and each of the four
    ways it can fail coming back with its own reason. Running this found a defect compiling had not:
    the executor returned a *refusal* (the type for a message that could not be read) where it owed a
    *result*, which carries no write id, so the uplink stayed silent and the cloud would have
    reported "not confirmed" about a write the edge knew had failed;
  - **the retain rule over a real broker** — a write the Gateway publishes is not retained, beside a
    configuration from the same publisher that is, so the difference is the write alone;
  - **the ACL, against Mosquitto** — the Gateway may ask any edge and read any edge's answer; an edge
    reads its own requests and not another's; and an edge cannot answer under another's name. Both
    rules were confirmed to fail when broken, which is what makes them tested rather than inspected.
    The confinement test needed a positive control before it meant anything: an ACL refusing
    everything satisfied it just as well, and the first version of that test passed under a mutation
    that removed the rule.
  - **the uplink's own handling of a message off a broker** — a write published onto the link, into
    the running service, out to a real device, and the answer back on the topic the cloud listens
    to. This was the last joint no test crossed and it was covered by compiling, which is the same
    gap the executor's defect hid behind. Restoring that defect now fails a test **here** as well as
    the executor's own, which is the point: the silence it caused was visible from both ends and
    only one of them was being watched.
  - **a device that accepts a connection and then says nothing** — the shape Phase 7's walk found in
    the *read* path, now executed on the write path too. It came back as a failure with a reason,
    which is what ADR-0023 requires, and it took **about twenty seconds**.

  **And that last one was a finding, not a tick — closed on 2026-10-05.** The executor has a
  ten-second deadline and `ModbusTcpDriver` sets a five-second socket read timeout, and **neither was
  what stopped it**: the failure arrived as a transport error — `Unable to read data from the
  transport connection` — after roughly twenty seconds. NModbus does not honour the cancellation
  token during a read, so a cancelled token does not interrupt one.

  **The cause was found and fixed, and it was neither of the two bounds.** NModbus's transport
  retries a failed request **three times by default**, so the driver's five-second timeout was worth
  four attempts — and `ModbusTcpDriver` never set `Retries`, so every comment in this repository that
  called the read bound five seconds was wrong by a factor of four. Measured before: 20.9 s. Measured
  after setting `Retries = 0`: **5 s**, which is one attempt times `ResponseTimeout`.

  The retry is off rather than tuned, and the reason is the write path: a retried **read** is safe and
  a retried **write** is a command sent twice. Most values tolerate that and a driver cannot know
  which ones do not — a device whose write increments, toggles or acknowledges is actuated twice by a
  helpful retry. A transport that cannot tell a read from a write has to take the safe side of both.
  What is given up is resilience to one corrupted packet, and the scan loop is the answer there: it
  asks again on its own schedule, and a tag that missed one scan reads Bad and then Good, which is
  what the quality field is for. A write worth retrying is worth an operator deciding to retry it.

  **Proved by mutation:** putting `Retries` back to 3 fails the dead-device test with `a dead device
  took 20.9 s to report; the driver's own bound is 5 s`. The test's assertion was widened to 15 s,
  having been a minute when the bound was not understood — a minute would have passed a regression
  that put the retry back.


### 2.1 ADR-0019 configuration provisioning, end to end

**This is the item that caused this file to exist.** Four documents said, in the
present tense, that the cloud's half was not built. It was: commits `cf457da`
(2026-09-28) and `487e07d` (2026-09-29) added it, and the prose written the
following morning did not notice. All four were corrected on 2026-10-01 —
`CLAUDE.md`, `HANDOVER.md`, `phase-plan.md`, `deploy/cloud/README.md`, plus the
same claim in `HANDOVER-archive.md` and in ADR-0019's own context.

- **Built — the cloud's half:**
  `src/Gateway/Provisioning/EdgeConfigurationBuilder.cs` derives one edge's
  devices, driver, settings and tag addresses from the running catalogue;
  `EdgeConfigurationPublisher.cs` publishes them retained on
  `{prefix}/{edgeId}/config`, republishes when the content hash changes and not
  otherwise, republishes everything after a reconnect, and empties the topic of
  a deleted edge; `GatewayApp.cs` registers it;
  `src/Modules/Drivers.Mqtt/EdgeConfigurationPayload.cs` is the versioned
  message both sides share.
- **Built — the edge's half:** `EdgeConfigurationSource`/`Consumer` subscribe to
  that topic over the connection the edge already holds, accept a payload whole
  or not at all, keep the last accepted one in the SQLite buffer, and apply a
  newer one by restarting acquisition. `Edge:Devices` is gone from the options,
  from `src/EdgeAgent/appsettings.json`, from the deleted
  `deploy/edge/edge.example.json` and from the edge Compose file.
- **Built — the deployment:** `deploy/cloud/docker-compose.yml` sets
  `EdgeProvisioning__Enabled: "true"`, the broker host and port, and mounts the
  Gateway's certificate; `deploy/cloud/mosquitto/acl` gives `scada-gateway`
  `topic write scada/edge/+/config` and each edge `pattern read
  scada/edge/%u/config`. `src/Gateway/appsettings.json` leaves it **off**, which
  is a developer's Gateway with no broker and not a statement about readiness.
- **Tested, and passing.** `EdgeConfigurationPublishingTests` runs the publisher against a real
  in-process MQTT server and asserts the retained publish, the late subscriber receiving it, no
  republication when nothing changed, and the emptied topic of a deleted edge — measured
  **3 passed, 0 failed**. `EdgeConfigurationPayloadTests` and `EdgeConfigurationTests` cover the
  payload and the assignment rules, also passing. `BrokerConfigurationTests` covers the delivery
  at the broker itself, against `deploy/cloud/mosquitto` in a container, including
  `The_Gateway_publishes_an_edges_configuration_and_that_edge_reads_it` and
  `An_edge_cannot_read_another_edges_configuration`, which is the ACL above: run on its own with
  Docker reachable, **9 passed, 0 skipped** (2026-10-01). In a whole-solution run those nine
  report as skipped, and §2.3 gives the reason.
- **Walked 2026-10-01**, on one machine, and it worked: the Gateway published three revisions of
  the edge's configuration, the edge accepted the last and answered `Connected to device Plant
  PLC.`, and 30 readings — 30 distinct times, every one `pushed` — arrived in the cloud database
  under the cloud's own tag id, the last measured by the edge and stored 0.19 s later. The numbers
  are in
  [`phase-7-manual-gate.md`](phase-7-manual-gate.md#since-the-walk-the-provisioning-delivered-and-read).
  Nothing on the plant machine named a device, an address or a tag id.
- **Found by that walk, and closed 2026-10-02** (this repository's PR #11). A device whose
  `driverKey` no edge driver answered to was accepted by the Gateway, derived, published, and
  refused only by the edge (`Device X needs driver 'y', which this edge agent does not have.`) —
  loud at the plant, silent in the cloud. ADR-0019 had left "whether an edge may be sent a device
  it cannot reach" to implementation; **§8 now decides it**. The edge declares the driver keys its
  own build has, retained on `{TopicPrefix}/{Edge:Name}/drivers` (`EdgeDriversPayload`, version 1,
  edge to cloud); the cloud records them (migration `0014`) and refuses, by name, a device assigned
  to an edge that has declared it does not have that driver — at the save that assigns it, and when
  the driver of an assigned device is edited. An edge that has **not** declared yet is accepted, and
  shown as having declared nothing rather than as having the Gateway's drivers; a declaration that
  arrives without the driver of a device already assigned to it is recorded in the audit trail with
  the device named, and the assignment is left alone.
- **Walked 2026-10-02**, on one machine, with the real cloud stack — real Mosquitto over real TLS, a
  real Gateway, a real edge agent, images built from PR #11 (`7f9692a`). Before the edge ran,
  `declaredDriverKeys` was `null` and a device whose driver the edge does not have (`mqtt`, which
  the cloud registers and no edge does) was **accepted** into it. The edge then declared two drivers
  at `15:36:07.811`, the cloud read them **0.019 s later**, `/api/edges` answered
  `["modbus-tcp","opc-ua"]` where it had answered `null`, the same save was then **refused 400**
  naming the edge and the drivers it has, a `modbus-tcp` device was accepted `201`, and the one
  device that had been assigned before the declaration was named in the log and in one audit row
  with a null actor. The numbers and the exact output are in
  [`phase-7-manual-gate.md`](phase-7-manual-gate.md#since-the-walk-driverkey-declared-by-the-edge-and-refused-by-the-cloud-2026-10-02).
  A walk on **two hosts** is §1.1's item, not this one.
- **Carried onto two hosts on 2026-10-02, and it worked there too.** The two-host walk
  ([the record](phase-7-manual-gate.md#the-walk-on-two-hosts-and-two-clocks-2026-10-02)) ran
  this path end to end for real: the edge started with **0 device(s)**, declared
  `["modbus-tcp","opc-ua"]` on the link it holds, accepted revision
  `sha256:a6dd9e23…` **derived by the cloud**, and logged `Connected to device Pump Station PLC`
  — with nothing on that machine naming a device, an address or a tag id. The declaration was
  audited twice (`17:03:01.772016`, and `16:59:02.72734` on first connect), `unreadableDeviceIds`
  empty both times. This was not a deliberate re-walk of §2.1 — the edge's configuration was
  simply how the walk got a device to read — so treat it as confirming evidence, not as a repeat
  of the walk above. **What it did not test:** a device being assigned *while* the edge is
  connected, or a configuration changing under a running edge. Both edges here accepted one
  revision and kept it.
- **Needs:** nothing beyond Docker to walk it again — the gate record's new section is the recipe.
- **Two flakes, seen once each on 2026-10-02, in the same loaded run.** With the whole Gateway test
  project running beside the other six assemblies — the step above added three in-process MQTT
  brokers to that parallel load — `StartupCheckTests.A_journal_ahead_of_the_build_is_refused_too`
  and `EdgeConfigurationPublishingTests.A_change_is_published_retained_to_that_edges_topic` each
  failed once. Each passed when its class ran alone, and the next whole-project run was green at
  **119 passed, 0 skipped**. Recorded rather than chased, for the reason the flake below is: a test
  that fails only under load makes "the suite is green" mean less than it looks. A fix for exactly
  this class — the broker's port taken at the bind rather than twenty seconds later — was opened as
  this repository's **PR #10 and closed without merging** (2026-10-02), so nothing about it is in
  `main` and the flakes above remain: whoever picks this up should decide whether to port that work
  or to say why it was closed.
- **Also found by the walk, 2026-10-02, and decided the same day:
  [ADR-0020](../architecture/decisions/0020-devices-with-no-tags.md).** A device with no tags is
  derived into a configuration the edge refuses whole. A device assigned to an edge before its
  tags existed travelled in the message, and the edge refused all of it (`device '…' has no
  tags`) — the reader is right, since a device with no tags is not a device, but the cloud
  published it without noticing, which is the shape of the finding this step closed. **The
  decision is that the derivation omits such a device and names it in the Gateway's log**, so
  the operator's natural order — create the device, assign it, add its tags — keeps working and
  the device reaches the edge when its first tag is saved. Nothing was lost when the walk met
  it: the edge kept reading the last configuration it accepted. **This is written but not
  walked** — see §2.0.

### 2.2 The database half of the suite, on this machine — **closed 2026-10-01**

- **What used to happen.** A native PostgreSQL 18 service holds port 5432, so
  anything connecting to `localhost:5432` reaches it rather than the container,
  and authenticates as nobody. Every test that needs a live database reported as
  **skipped**, which is the design (a skipped test proves nothing) but meant a
  "green" run on this machine was only the part that needed no database.
- **What closed it.** The test helpers take `SCADA_TEST_DB_PORT` as well as
  `SCADA_TEST_DB_HOST` (`tests/Persistence.Tests/TestDatabase.cs`,
  `tests/Gateway.Tests/Hosting/TestDatabases.cs`), and the development Compose
  file publishes `${SCADA_DB_PORT:-5432}`, so a container can sit on 5433 beside
  the native server without either giving way. **The native service was never
  stopped** — that was the operator's call to make, and not this repository's.
- **Measured the same day, beside the running native server:** 401 passed, 0
  skipped, 0 failed across seven projects (§2.3).

### 2.3 What a run on this machine measured, 2026-10-01

For the reason in §2.2, read the skipped column before the passed one. `dotnet test
ScadaDarbox.slnx`, **no database reachable**, all seven projects exit 0:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| `ScadaDarbox.Core.Tests` | 105 | 0 | 105 |
| `ScadaDarbox.Drivers.Modbus.Tests` | 27 | 0 | 27 |
| `ScadaDarbox.Drivers.OpcUa.Tests` | 13 | 0 | 13 |
| `ScadaDarbox.Drivers.Mqtt.Tests` | 47 | 0 | 47 |
| `ScadaDarbox.EdgeAgent.Tests` | 24 | 0 | 24 |
| `ScadaDarbox.Persistence.Tests` | 5 | 67 | 72 |
| `ScadaDarbox.Gateway.Tests` | 45 | 67 | 112 |
| **all seven** | **266** | **134** | **400** |

**The same suite, the same day, with a database reachable on 5433** — a throwaway container
beside the native PostgreSQL, `SCADA_TEST_DB_PORT=5433` — and nothing skipped at all:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| `ScadaDarbox.Core.Tests` | 105 | 0 | 105 |
| `ScadaDarbox.Drivers.Modbus.Tests` | 27 | 0 | 27 |
| `ScadaDarbox.Drivers.OpcUa.Tests` | 13 | 0 | 13 |
| `ScadaDarbox.Drivers.Mqtt.Tests` | 47 | 0 | 47 |
| `ScadaDarbox.EdgeAgent.Tests` | 24 | 0 | 24 |
| `ScadaDarbox.Persistence.Tests` | 72 | 0 | 72 |
| `ScadaDarbox.Gateway.Tests` | 113 | 0 | 113 |
| **all seven** | **401** | **0** | **401** |

The client's own suite, `npm test` in `src/Web`: **53 passed, 0 failed**. The previously
recorded figures — 194 passed / 133 skipped with no database, and 311 / 17 with one, both on
2026-09-27 — are in `HANDOVER-archive.md`; the counts grew because the provisioning work added
tests, not because tests were lost.

### 2.4 The same suite after ADR-0019 §8, 2026-10-02 — **425 passed, 0 skipped**

This is the baseline to compare against, and it is the first run that includes the tests §8
added (`EdgeDriverDeclarationsTests`, `EdgeDriverRefusalTests`, `EdgeDriversPayloadTests`,
`EdgeAssignmentTests`, `BrokerConfigurationTests`) with a database reachable, so nothing in it
is skipped. `dotnet test ScadaDarbox.slnx` with `SCADA_TEST_DB_PORT=5433`, beside the native
PostgreSQL on 5432, **exit 0**:

| Project | Passed | Skipped | Total |
|---|---|---|---|
| `ScadaDarbox.Core.Tests` | 105 | 0 | 105 |
| `ScadaDarbox.Drivers.Modbus.Tests` | 27 | 0 | 27 |
| `ScadaDarbox.Drivers.OpcUa.Tests` | 13 | 0 | 13 |
| `ScadaDarbox.Drivers.Mqtt.Tests` | 61 | 0 | 61 |
| `ScadaDarbox.EdgeAgent.Tests` | 25 | 0 | 25 |
| `ScadaDarbox.Persistence.Tests` | 75 | 0 | 75 |
| `ScadaDarbox.Gateway.Tests` | 119 | 0 | 119 |
| **all seven** | **425** | **0** | **425** |

The client's suite, `npm test` in `src/Web`: **60 passed, 0 failed, 0 skipped**.

**And the three flakes did not appear.** §2.1's two and §2.3's one were all seen exactly once
under a loaded parallel run. This run was the same shape — seven assemblies at once, the walk's
containers beside them, the whole Gateway project at 119 — and all three passed. That is one
green run, not a fix: none of the three has been made deterministic, and PR #10 (the broker's
port taken at the bind rather than twenty seconds later) is still closed. Treat this as a
baseline that a future red run can be compared against, not as evidence the flakes are gone.

**Corrected the same evening: the 425 was a lucky run, and this is not the baseline to trust.**
Six further whole-solution runs on the same code — three with a change, three on the commit
without it, same machine, the same test database, the same loaded stack beside them — produced
**one or two failures in every one of them**, and never the same pair twice:

| Test | Seen |
|---|---|
| `Persistence.Tests.EmbeddedScriptsTests.The_gateway_check_refuses_a_build_without_scripts_against_a_database_never_migrated` | 5 of 8, failing in `TestDatabase.DropEmptyAsync` with `NpgsqlException … TimeoutException: Timeout during reading attempt` |
| `Persistence.Tests.DuplicateNameMigrationTests.Duplicates_already_in_the_database_are_renamed_audited_and_then_indexed` | 3 of 8, on the runs where the change was not applied |
| `Gateway.Tests.EdgeDriverDeclarationsTests` — three different tests across runs | 6 of 8, `Assert.Single() Failure: The collection was empty` on the audit row |
| `Gateway.Tests.EdgeConfigurationPublishingTests.An_edge_that_is_deleted_has_its_configuration_emptied` | 1 of 8 |
| `EdgeAgent.Tests.ConfigurationLinkTests.A_configuration_the_edge_cannot_read_is_refused_whole_and_changes_nothing` | 1 of 8 — in the one assembly no change touched at all, which is the clearest evidence that this is the load and not the code |

Every one of them passes when its project is run alone: each of the classes above was run in
isolation, three or four times each, and never failed once.

**No claim about the code follows from this, and one claim about the suite does.** The failures
are not caused by the change measured beside them: the same tests fail on `6c373c6` with none of
it applied, and the pair that fails moves between runs. What they show is that **a whole-solution
run on this machine is not currently green**, so "the suite passes" is not evidence anyone can
use until it is — which is the same conclusion §2.1 reaches about its own two flakes, now with a
reproduction rate behind it. The load is the variable that was not there before: seven assemblies
in parallel, the cloud stack, the walk's simulator, and a test database all on one host.

**What to do about it is not decided, and it is not ADR-0020's business.** The candidates are the
three this file already carries: make the deadlines signals rather than wall-clock waits (PR #10's
shape, closed without merging), give the database-heavy tests their own database rather than one
shared one they create and drop under each other, or serialise the assemblies. Whoever picks it up
should start from the reproduction above — and should note that a *skipped* test proves nothing,
so this is not fixed by making the database unreachable.

### 2.5 The flakes, diagnosed and fixed — **closed 2026-10-02**

Two causes, both of them the same mistake in two places: a wall-clock deadline standing in for a
signal. Neither was in the code under test.

**1. A client-side timeout on a server-side operation.** `ServerConnectionString` in both test
helpers (`tests/Persistence.Tests/TestDatabase.cs`, `tests/Gateway.Tests/Hosting/TestDatabases.cs`)
carried `Command Timeout=10`. That connection does `CREATE DATABASE` and `DROP DATABASE … WITH
(FORCE)` and nothing else, and on a loaded server those are slow — seven assemblies at once,
several of them creating and dropping databases, each migration running hundreds of statements.
The **client** gave up while the server was still working, which surfaced as `NpgsqlException …
TimeoutException: Timeout during reading attempt` inside `DropEmptyAsync`. The change is
`Command Timeout=0` on that connection and that connection only: it is an administrative
connection with no user waiting on it, so it has nothing to protect with a deadline. The
`Timeout=3` beside it stays, because connecting fast and failing fast is still right and the
availability probe depends on it. Per-database connection strings are untouched — those carry a
user's query and a deadline is appropriate there. **Honesty about the evidence:** this cause is
*read off the failure*, not proven load-bearing. The exception names a read timeout on a `DROP`,
and the connection it names carried the ten seconds; but when the ten seconds was put back, three
whole-solution runs and six project-only runs were all green, so the failure could not be
reproduced to order. What reproduced was the *rate* — one or two failures in eight consecutive
runs under the load this session's own concurrent commands were adding — and that load is not
something a later reader can summon on demand.

**2. A wait on the wrong step of a chain, in `EdgeDriverDeclarationsTests`.** `EdgeDriverDeclarations`
records the declaration, reloads the catalogue, and *then* appends the audit row. All three tests
waited only for `Catalogue.Declarations.Count == 1` and then asserted on `Audit.Entries`, so the
assertion was racing the write it checked. Each test now waits for the audit row too. This is the
race the whole file's `WaitUntilAsync` exists to avoid, applied to the last step rather than the
first. **This one is a real race and the fix is not a guess:** the failure was
`Assert.Single() Failure: The collection was empty` — the collection being `Audit.Entries`, the
row the test had never waited for — and the code path appends it after the reload the test did wait
for.

**Measured.** Eight whole-solution runs before either change — three of them on a commit with no
change applied — produced one or two failures each, never the same pair twice. **Seven consecutive
whole-solution runs after them were green**, at **429 passed, 0 skipped** across seven projects,
with the client's 60 passed beside them. The count is 425 plus the four ADR-0020 tests.

**And it stayed green.** ADR-0021 added twenty more tests and five more whole-solution runs were
green, at **447 passed, 0 skipped**, with the client's 63. The two flakes above have not been seen
since the fix.

**Measured again 2026-10-05, after Phase 8 walk's four defects were closed: 543 passed, 0 skipped across
seven projects**, with the client's 63. Whole-solution runs produced it as it grew: **465** before the
edge-side tests, which is the baseline above plus ADR-0023's cloud and wire halves (21 payload tests
in the MQTT module and 7 router tests in the Gateway, one existing Gateway test rewritten to assert
the routing instead of the refusal it replaced); then **492** after the edge's half went in (7
executor tests against a real Modbus slave); then **498** after the broker rules (6 more: 4 write-ACL
tests against a real Mosquitto, and 2 for the retain rule); then **501** after the uplink's own
handling of a write message (3 tests, the last joint nothing had crossed); then **502** after a
device that accepts a connection and then says nothing. The intermediate numbers
were undercounted in an earlier draft of this note by six, because it tried to reconcile them by
adding up per-project deltas instead of reading what the runs printed; the printed numbers are these
and the deltas are not offered as corroboration.

**One flake was seen on 2026-10-03, and it is now diagnosed and fixed rather than left as a
sighting.** `ScadaDarbox.Gateway.Tests` failed once in a whole-solution run and the failing test's
name was not captured before the output scrolled; its project then passed 143 of 143 on its own and
149 of 149 in the next whole-solution run. That was recorded honestly as *a sighting and not a
diagnosis*, and it was the right thing to write at the time — but the sighting was one instance of a
**pattern**, which is why hunting for the named test alone would not have found it.

**What it was, established on 2026-10-05.** Every failing test had the same shape: something
published a **retained** message, a subscriber connected afterwards, and the message never arrived
inside a twenty-second window. Four different tests, in two assemblies, over the course of the hunt —
`EdgeConfigurationPublishingTests` (three separate test methods), `ConfigurationLinkTests`,
`EdgeWriteUplinkTests`, `MqttDataProtectionKeysTests`' neighbours and others — which is exactly why
three attempts to name "the" failing test failed: **no single test was wrong.**

The evidence, from one captured failure, three statements together:

- the publisher logged `Connected to the broker at 127.0.0.1:59949` and `Published the configuration
  of edge edge-a to scada/edge/edge-a/config: 1 device(s)`;
- `TestBroker.RetainedTopicsAsync()` returned `[scada/edge/edge-a/config]` — the broker **held** it;
- the subscriber reported `connected=True` with `sent=0`, for the whole twenty seconds.

That is [dotnet/MQTTnet#1353](https://github.com/dotnet/MQTTnet/issues/1353), *"Retained messages are
not sent to new subscribers"* — reported with these exact symptoms (the server logs the retained
messages, the client receives nothing, *"maybe it can be some timing issue?"*) and closed as fixed in
4.0. It is still reachable at 5.2.0.1603 under whole-suite load. **It is not this project's code**:
the publisher set the retain flag (ADR-0019 §4) and the broker stored it. It is also **not the
product's broker** — that is Mosquitto, and nothing in this repository ships MQTTnet's server, which
exists here only as a test double.

**The fix, and the rule it leaves.** A test waits for what it owns. `EdgeConfigurationPublishingTests`
waits on the broker's retained set — the authoritative statement that the cloud published and it was
retained — and reads the configuration back out of it, so "the changed configuration" and "the emptied
configuration" are one wait with different expectations; delivery to a late subscriber is still
asserted, in the single test whose subject that is. `ConfigurationLinkTests` waits for the edge's
subscription on the broker *before* publishing, so the message reaches a live subscription instead of
being replayed to one that did not exist. The reasoning is written on `TestBroker` in all three test
projects, because the next broker test written here will reach for the same shape.

**Measured: 30 consecutive whole-suite runs, 540 tests each, zero failures.** The rate before was one
failure in every 3 to 12 runs. The one place the limitation remains is
`A_configuration_published_while_the_edge_was_away_is_applied_when_it_connects`, whose **subject is**
retained replay — it is honest about what it depends on and it is the only such test left.

A separate race was found and fixed while writing the uplink tests: a write is not retained, so one
published in the moment between the uplink connecting and subscribing is genuinely gone, and a test
that published once was asserting on that race rather than on the product. It republishes until
answered, and the reason is recorded in the test.

**One mass failure during the hunt was the environment, not anything here.** A Docker Desktop restart
stopped the test database mid-run and produced whole-solution runs with 11 and 17 failures at once,
every one of them `57P01 terminating connection due to administrator command` or `57P03 the database
system is shutting down`. It is recorded so that the next mass failure of that size is not mistaken
for a defect: **read the error, not the count.** Three other things the same hunt found are real and
fixed — a port chosen and then bound (a documented race, reproduced), a three-second *connect* deadline
that was too short for thirty parallel test classes, and the same deadline missing entirely from the
shared connection builder.

**Not claimed:** that no flake remains anywhere. What is claimed is that the diagnosed race is fixed,
that the timeout was wrong on its own terms whatever its share of the blame, that the retained-delivery
pattern is understood rather than worked around blindly, and that the reproduction rate went from 1 in
3–12 red to **30 of 30 green** on the same machine, the same stack and the same test database. One
shared database that several classes create and drop under each other is still the shape of the design,
and a deadline that is a wall clock rather than a signal is still how `WaitUntilAsync` is written.


What this run does **not** say: nothing above needed TimescaleDB, so the
database-enforced guarantees (append-only tables, the unique-name indexes, the
migrator's lock) were **skipped**, not verified. Only a run with a reachable
database proves those.

**Found and fixed the same day: a skip that named the wrong fact.** With Docker reachable
(`docker version` answered `29.8.0`), the Docker-gated tests reported as *"Docker is not
available ('docker version' failed)"* in a whole-solution run while passing nine of nine when
run on their own. The cause was the probe, not the machine: `BrokerFixture.DockerAvailable`
(`tests/Gateway.Tests/Hosting/BrokerFixture.cs`) was a process-wide `Lazy<bool>` that ran
`docker version` with its output redirected and **waited fifteen seconds**; anything slower — a
loaded machine, a daemon just starting, seven test assemblies at once — was recorded as an
absent daemon, and because it was a `Lazy`, that one observation decided every Docker test in
that assembly.

It is classified now rather than guessed: `ToolProbe` reports Available, Absent, TimedOut or
Blocked, `ToolAvailability` keeps only the answers that are facts about the machine and asks
again after a timeout, and every skip message names which of the four happened. The database
probes carry their exception in the same way — which is how the 67 skips in the first table
came to say `28P01: password authentication failed for user "scada"` instead of a sentence that
blamed the machine. With that in place the same solution run reports **401 passed, 0 skipped**.

**One flake, seen once, recorded rather than chased.** In the first full-solution run after the
three pull requests merged, `EdgeConfigurationPublishingTests.A_configuration_that_has_not_changed_is_not_published_again`
failed with `System.TimeoutException: Timed out waiting for the first configuration to be
published` — a twenty-second window — while all seven assemblies ran in parallel beside the walk's
five containers. It then passed three times out of three with that class alone, passed again as
the whole project (113/113), and a repeat of the same full run was green. That test takes a port
by asking the OS for a free one and releasing it before binding (`FreePort()` in that file), which
another process on a loaded machine can win in between, and its wait is a wall-clock deadline
rather than a signal. It matters for the reason the probe fix above does: a test that fails only
under load makes "the suite is green" mean less than it looks.

## 3. Waits for a decision, before any code

An ADR is changed by a new ADR, never edited into a different decision; and a
task that seems to need one of these raised in the design conversation, not
decided inside an implementation pull request.

| Item | Where it is recorded | What it waits for |
|---|---|---|
| Alarm flapping — deadband, on-delay | `phase-plan.md`, "Deferred out of Phase 5.5" | **closed 2026-10-05 by [ADR-0025](../architecture/decisions/0025-an-alarm-waits-before-it-announces-itself.md)** — both settings implemented, the schema, the API and the threshold form; see §2.0e |
| Filtering the journal (tag, event type, time) | same place | **closed 2026-10-05** — server-side, by tag and by event type; see §2.0e |
| Routing a tag write to an edge-assigned device | ADR-0019, Consequences | **closed 2026-10-03 by [ADR-0023](../architecture/decisions/0023-routing-writes-to-an-edge.md)** — see §2.0 for what it still owes a walk |
| The Modbus 5 s response bound: driver constant or per-device setting | `phase-plan.md`, Phase 7 note | **closed 2026-10-05** — a per-device `responseTimeoutSeconds` setting, defaulting to 5 s; see §2.0f |
| Alarm notification channels and escalation policy | `phase-0-architecture.md`, "Explicitly open" | a design decision, then an ADR |
| TimescaleDB continuous aggregates and native compression | ADR-0006 | the legal review ADR-0006 asks for; the code deliberately does not use them |
| Rollback of a schema migration (down-scripts) | ADR-0012, ADR-0014, `phase-plan.md` Phase 6 | **stays forward-only by decision**; the guide's backup-and-restore is the answer |
| Authoring a screen: drag and drop, a live preview while editing | `phase-plan.md`, Phase 8 "What is left for the next slice" | **the preview is closed 2026-10-05**; drag and drop waits for a slice — see §2.0b |
| The preview's trend saying "Reading…" instead of drawing | same place | **closed 2026-10-05** — history is keyed by tag now, so a draft can ask for it; see §2.0g |
| Saying that a save is immediate | same place | **closed 2026-10-05** — a sentence under the buttons; see §2.0g |
| Writing a tag from a screen | same place | **closed 2026-10-05 by [ADR-0026](../architecture/decisions/0026-operating-from-a-screen.md)** — one component writes, the server decides whether to offer it; see §2.0h |
| Scripting (Jint), reporting | `phase-plan.md`, "Later (not yet scoped)" | a phase that creates a concrete need |
| CI — any pipeline at all | not scoped by any phase | a phase that wants it; today every gate rests on a recorded hand walk |

## 4. Environment, not project

Recorded so a later session does not mistake it for a defect in the code.

- **Nothing in history is rewritten on purpose.** Two commits carry Macedonian
  messages (`487e07d`, `fc95f04`) and one carries the message `msg` under 1,491
  lines of real work (`cf457da`). They are on `main` and pushed; rewriting
  pushed history to tidy a message would cost more than the blemish. Leave them.
- **`report_deepseek.md`** is a generated repository report, untracked until
  2026-10-01 and now named in `.gitignore`. It duplicates documents that live
  here, in an encoding that is not ours; the originals are the ones to read.
- **Docker is reachable in more than one way, and they disagree.** `docker` on
  the Windows PATH resolves to Rancher Desktop, `wsl docker` is refused, and a
  sandboxed process cannot open the Docker pipe at all — so
  `RequiresDockerFact`/`RequiresDockerComposeFact` tests report as skipped from a
  sandboxed run. That is an environment fact about the machine, not about the
  tests: with the daemon reachable they run, as the Phase 7 suite record shows.
- **Line endings.** Committed blobs are LF and the working tree is CRLF. Use
  Windows git; strip the CRs before running a script through WSL's `bash`
  (`tr -d '\r' < script.sh > /tmp/script.sh`), which `deploy/build-images.sh`
  line 27 needs on a fresh checkout.
- **`deploy/.env` and `deploy/cloud/.env` exist locally and are correctly
  ignored.** The cloud stack was once started from a second copy of the
  repository inside WSL, so a running container does not necessarily reflect the
  working tree being read.
- **`OPENSSL_CONF` on this machine points at a file that does not exist**, left by
  another installation: `C:\Program Files\PostgreSQL\psqlODBC\etc\openssl.cnf`. Every
  `openssl` invocation therefore dies before it starts, which cost the 2026-10-01 walk its
  first attempt. **`deploy/cloud/certs.sh` no longer hides it**: it asks `openssl version`
  before anything else and prints openssl's own words when that fails, instead of sending
  each step's reason to `/dev/null` and exiting in silence. Either way, clear the variable
  (`Remove-Item Env:\OPENSSL_CONF`) before making certificates on this machine.

## 5. Documentation that described an older tree

Corrected on 2026-10-01. Each was a present-tense fact that had moved on, and they are listed
here so the same drift is caught faster next time. `HANDOVER.md`'s structural inventory is the
part of this repository most likely to go stale: it describes the tree rather than recording a
decision, and the decisions have ADRs to keep them honest while the tree has nothing.

| Where | Said | Actually |
|---|---|---|
| `HANDOVER.md`, the tree | migrations `0001…0011` | `0001…0013` — 0012 edge assignment, 0013 edge link device |
| `HANDOVER.md`, the tree | `ADR-0001…0018` | `ADR-0001…0019` |
| `HANDOVER.md`, `tests/` | "the table in §1" | the suite table is in `HANDOVER-archive.md`, not §1 |
| `HANDOVER.md`, limitations | the edge's list is copied by hand, and the answer is not built | both false — `Edge:Devices` is gone and the answer is built |
| `HANDOVER.md`, limitations | "133 of 327 .NET tests … skipped" | 134 of 394, measured 2026-10-01 (§2.3) |
| `CLAUDE.md`, solution layout | `Drivers.Mqtt/ (Phase 4)` | Phase 7 — Phase 4 deferred MQTT, as the same file says |
| `phase-7-manual-gate.md`, step 9 | lower the buffer bound in the file `SCADA_EDGE_CONFIG` names | that file and variable are gone; the bound is a configuration key |
| `HANDOVER.md`, trap 1 | the test projects hard-code 5432, so free the port | `SCADA_TEST_DB_PORT` and `${SCADA_DB_PORT:-5432}`; 401 passed, 0 skipped beside the native server |
| `HANDOVER.md`, trap 2 | there is no `docker` CLI on the Windows PATH | it is there (Rancher Desktop, 29.8.0); `wsl docker` is the one refused |
| `HANDOVER.md`, trap 3 | "the repository has no `.gitattributes`" | a root `.gitattributes` was added on 2026-09-27 (`527654a`) |
