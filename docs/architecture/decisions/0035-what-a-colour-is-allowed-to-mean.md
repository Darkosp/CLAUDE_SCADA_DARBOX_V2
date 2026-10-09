# ADR-0035 — What a colour is allowed to mean

**Status:** Accepted
**Date:** 2026-10-09

## Context

**Red means two things in this product, and they are drawn in two shades nobody can name apart.**

| | |
|---|---|
| `--status-bad-ink` `#9b2c25` on `--status-bad-bg` `#fbe6e6` | *this reading cannot be trusted* |
| `--alarm-border` `#f0c7c4` on `--alarm-surface` `#fff6f5` | *a limit has been crossed* |

Found by the standards audit of 2026-10-08 (`standards-baseline.md` §4.7) and recorded as a finding
rather than patched, because changing what a colour means is a decision. ADR-0034 then deferred giving
alarm **priority** any colour for exactly this reason — *"a third claim on a channel that already
carries two"* — and in doing so made this ADR the thing standing in front of the rest of the alarm
work: the banner, the flood case, escalation and ISA-18.2's own distribution metric all want to be
seen, and none of them can be until the channel is sorted out.

### What the standards decide, and it is more than expected

**Colour is for the abnormal.** The high-performance HMI practice ISA-101 codifies is that process
graphics are **grey**, and equipment is *"gray when normal, colored only when abnormal"*
([Control.com, *Going Gray*](https://control.com/technical-articles/going-gray/)). Alarm colours in
particular should be *"bright, intense colors that are not used in any other part of the display"*
([Tatsoft's ISA-101 guide](https://docs.tatsoft.com/display/FX/ISA-101+HMI+Compliance+How-to)).

**Red is the colour of *act now*.** IEC 60073's coding convention gives red to an emergency needing
immediate action, yellow/amber to an abnormal situation needing monitoring or intervention, and green
to normal — with green's use **optional** ([Schneider's summary of the convention, citing
IEC 60204-1](https://www.se.com/be/en/faqs/FA146188)).

**Colour is never the only carrier.** Every source says it: *avoid using colour as the only indicator*,
pair it with shape or label, and be careful with red/green pairings, which are the common
colour-blindness failure.

**What the standards do *not* decide: the colour of bad data.** Searching ISA-101, EEMUA 191 and
IEC 60073 found no rule for *"this reading cannot be trusted"* — the closest material is vendor
behaviour, and it disagrees with itself (one SCADA product defaults bad-quality alarms to **white**,
optionally blinking). **That part is ours**, and this ADR says so rather than implying a standard
backs it.

**None of these documents was read.** ISA-101.01 and IEC 60073 are paywalled and EEMUA 191 is sold;
the above is from the public secondary sources named, which is why conventions are described and no
clause is quoted.

## Decision

**1. Colour means *this needs attention*. Everything else is neutral.**

Grey, and the near-black hue-less accent chosen on 2026-10-06, are the normal state of this interface.
A colour appearing is itself the signal. This is already most of what the client does and it is written
down here so the next feature does not reach for a colour to mean *this is a button* or *this is
selected*.

**2. Red is the alarm's, and only the alarm's.**

Nothing else on a screen is red at full strength. Not a quality, not a validation message, not a
destructive button. IEC 60073 gives red to *act now*, and ISA-101 asks that an alarm colour appear
nowhere else — a rule that is worth nothing if it is honoured three quarters of the time.

**3. An alarm's colour follows its priority, not the fact that it is an alarm.**

| Priority | How it is drawn |
|---|---|
| **High** | red — the strongest thing on the screen |
| **Medium** | amber |
| **Low** | **no colour**: a neutral row, like everything else that is not demanding action |
| **Not yet rationalised** | neutral, and labelled, as ADR-0034 §2 requires |

**Low gets nothing, and that is the whole point.** ISA-18.2's target distribution is roughly 5% High,
15% Medium and **80% Low** — so colouring every alarm paints four fifths of the list, which is the
failure the distribution exists to prevent, arriving through the palette instead of through the
priorities. Today every alarm row is the same red whatever its priority, so a Low alarm shouts exactly
as loud as a High one.

**4. Quality and priority are different questions and get different channels.**

They are orthogonal: *can I trust this number* is not *does this demand action*. A Good reading can be
in alarm; a Bad reading has no value to compare, which is why ADR-0003 gives it none and ADR-0013 never
lets it clear an alarm.

So they are separated by **what is coloured**, not by hue alone:

- **An alarm colours its row or its surface** — area, and the strongest colours available.
- **A quality colours a small labelled badge and nothing else** — the four values of ADR-0003 keep
  distinct hues, because four states must stay tellable apart, but a badge is metadata about one
  reading and never takes the weight of a process condition.

**Bad quality therefore loses the strong red it has now.** A reading you cannot trust is not an
emergency; under IEC 60073's own vocabulary it is much closer to *abnormal, needs looking into*. It
keeps a red **hue** inside its badge so it stays distinguishable from amber Uncertain and grey
no-reading, and it gives up the **area and intensity** of red, which belong to decision 2.

**5. Nothing is carried by colour alone.**

Every coloured thing also says what it is in words: the quality badges already do, and the priority
label added on 2026-10-09 does. A screen read in greyscale, or by somebody who cannot separate red from
green, must lose nothing but speed.

**6. Red and green never mean opposite things about the same object.**

The common colour-blindness failure, and this product has a specific exposure: ADR-0027 symbols draw
*running* and *stopped*, and the obvious wrong move is red-stopped / green-running. A symbol's state is
carried by its **shape** (ADR-0027 already decides this), and quality overrides it.

**7. What this does not decide.**

- **Flashing or blinking.** The convention is to blink an unacknowledged alarm, and it is an
  *annunciation* decision — it belongs with the notification channels `phase-0-architecture.md` still
  lists as open, because something that moves on a wall is in the same family as something that makes
  a noise or sends a message.
- **Exact hex values**, beyond the AA contrast floor `theme-contrast.test.mjs` already enforces in both
  themes. The rules above constrain which token a thing uses; the values stay in `styles.css`.
- **A per-deployment palette.** ISA-101 expects a *site* to own its style guide, and a plant whose
  operators are trained on a different scheme has a real claim. Nobody has asked, and a second palette
  is a token set rather than a decision — which is the position ADR-0027 §6 takes about a second symbol
  drawing.

## Consequences

- **Every alarm row changes.** A Low or unrationalised alarm stops being red, which is the point and
  will look like something is missing on the first day. The banner stops being a wall of red.
- **The Bad quality badge changes**, and ADR-0003's model does not. Four qualities, four names, four
  distinguishable badges; what changes is how loudly one of them is drawn.
- **ADR-0034 §4's deferral is discharged**, and that sentence is amended in the same pull request as
  the implementation, per the rule in `decisions/README.md`.
- **`theme-contrast.test.mjs` grows**, because a token moving is a pair that has not been measured —
  the rule that file states about itself.
- **This cannot be judged from a diff.** It is a decision about what a screen looks like at a glance,
  and it ends in the only place such decisions can: somebody standing in front of it. Two walks in two
  days have each found something no test could.

## Alternatives considered

**Give priority its own hue family and leave quality alone.** The obvious move, and it fails decision
2's test: it would put a third meaning on the same channel rather than sorting the two already there,
and the screen would end up with red quality beside red priority beside red alarm.

**Colour every alarm and use shade for priority.** Refused by decision 3's arithmetic. Four fifths of
alarms are Low by design, so this paints four fifths of the list and then asks a reader to rank shades
of one hue — which is the discrimination task the distribution target exists to avoid.

**Take bad quality out of colour entirely** — hatching, a struck-through value, a neutral badge.
Attractive, and genuinely closer to high-performance practice, but it collapses Bad toward *no reading*
and ADR-0003 needs its four values apart. Hue inside a badge is the cheaper way to keep them separate,
and decision 4 takes the area away instead, which is where the conflict actually was.

**Wait for a real operator before changing anything.** Tempting, since this is a judgement about
appearance. Refused because the current state is not neutral: it is actively wrong against two
standards, and leaving it means the next three alarm features are built on a channel that is already
full.

## Verified in review by

- **No `--status-bad-*` token is used on a surface or a row** — only inside a badge; a test over the
  stylesheets fails if one appears elsewhere.
- **Nothing outside the alarm rows uses the red alarm tokens**, in either theme.
- A **High** alarm draws red, a **Medium** amber, and a **Low** and an **unrationalised** one draw
  neutral — asserted per priority, because a rule with four cases and one test is a rule with three
  untested cases.
- **Every coloured element has a word beside it**: a test that fails if a quality badge or a priority
  marker renders with no text.
- Both themes clear the AA floor for every pair this moves, including the pairs that are new.
- **Walked.** The alarm list is looked at with alarms of all three priorities standing at once, and the
  question asked out loud is whether the High one is the thing the eye lands on first. ADR-0031,
  ADR-0033 and ADR-0034 each shipped correct and unwalked, and the walk found something every time.
