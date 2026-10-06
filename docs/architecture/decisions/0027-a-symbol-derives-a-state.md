# ADR-0027 — A symbol is a component kind with a state it derives, not a picture

**Status:** Accepted
**Date:** 2026-10-06

## Context

ADR-0024 gave a screen five component kinds and closed the set: *"The five kinds will not be enough.
The first real screen will want something else, and the answer is a new ADR adding a kind — not a
generic component, and not a scripting escape hatch."* This is that ADR, and it is the first time the
sentence has been acted on.

What prompts it is concrete rather than aspirational. An operator reading a screen that says

```
Pump Running        GOOD
ON
```

has to translate a word into a machine. An operator looking at a pump symbol that is turning does not.
The value is the same and the quality is the same — what changes is that one of them is a picture of
the plant and the other is a row in a table. Writes were settled by ADR-0026, so a symbol can also
carry the control that starts it.

The pressure to do this badly is real and this project named it in advance: an HMI is where a project
gives in and adds a scripting escape hatch. The whole difficulty is that *"the pump turns when it is
running"* sounds like a rule and is not — it is a **mapping from a tag's value to a named state**, and
the decision is what that mapping may contain.

## Decision

**1. `symbol` is a new component kind, bound to one tag, and it derives a named state from that tag.**

One new kind, not a generic drawing surface. It reads exactly as a `value` does — readably, in a Site
the reader may see, by tag id — and then instead of printing the reading it draws the symbol in the
state the reading maps to. Everything ADR-0024 guarantees about a component still holds: it cannot
print a value with nowhere for the quality to go, and a binding the reader may not see renders as
unreadable rather than vanishing.

**2. The state mapping is a list of rules, ordered, held on the component, with the first match
winning.**

Each rule is *this named state when the tag's value is equal to / above / below this*, and one rule may
be marked as the fallback. The author edits the list; the server stores it; the client evaluates it.

```jsonc
// A pump that is Running when its tag is true, Stopped when false, and otherwise has no state.
"states": [
  { "when": "equals", "value": "true",  "state": "running" },
  { "when": "equals", "value": "false", "state": "stopped", "otherwise": true }
]
```

An ordered list of declarative conditions rather than an expression, and that is the decision this ADR
exists to make. An expression would be shorter to write and would be a language: it needs parsing,
error reporting, a story about what an operator may type, and eventually a debugger. A list of
comparisons needs a form with a dropdown in it, and it is testable without a browser — the same trade
ADR-0024 made when it closed the component set.

**3. A value with no matching rule and no fallback resolves to `unknown`, and `unknown` is drawn.**

Falling through to any state would be inventing a fact about a plant. The symbol for `unknown` is
deliberately different from every mapped state, because "the mapping does not cover this reading" is
something an author needs to see and fix.

**4. Quality overrides the state.**

**A reading whose quality is not Good produces no state at all, whatever its value.** The symbol is
drawn in its `stale` or `bad` form and says which, and no animation runs.

This is ADR-0003 applied to a picture, and it is the most important decision here. A boolean tag that
has gone Bad still carries its last value; drawn as a running pump, that is a screen showing a plant
state nothing measured — the exact failure ADR-0003 exists to prevent, in the medium where it is
hardest to notice. **A pump that is turning is a claim about the world**, and this ADR refuses to make
it on Bad data.

**5. Animation is derived from the state, and it is bounded.**

A symbol has a fixed set of states; each is drawn one way, and a state may be declared as animating. **A
continuing value never drives an animation** — no rotation proportional to a flow rate, no fill
proportional to a level. Those are `trend` and `value` work, and the reason is that continuous
animation on a control-room screen is a lie about precision: a reader cannot measure a rotation, so it
reads as decoration while appearing to be a reading.

Turning is done in CSS with a duration the state carries, and it respects `prefers-reduced-motion`.

**6. One symbol in this slice: the pump.**

The vocabulary is deliberately one shape. A symbol is a drawing plus a set of states plus a mapping, and
the questions above — what a mapping may contain, what quality does, what animates — are answered by one
of them. Adding a tank, a valve or a motor is then a drawing and a state list, not a decision, and the
next symbol can be added when a real screen wants it. `phase-plan.md` carried this as the first step
towards a mimic, and the point of taking it first is exactly that it does not commit to the canvas,
free positioning or pipe routing that a mimic needs.

**7. A symbol is not a control.**

It shows. Writing is `value`'s job and stays there (ADR-0026). A pump symbol that is also a button is a
control whose whole appearance is arbitrary, and the one thing ADR-0026 was careful about — that the
thing you press says what it will do — is exactly what a picture cannot say.

## Consequences

- **The component set is no longer five, and the sentence in ADR-0024 that said it would not be enough
  has been answered rather than deleted.** A sixth kind is a smaller change than it sounds because the
  hard parts — storage, the API, the read view, the resolver, the twelve-column grid — are agnostic
  about what a component draws.

- **A screen can now be wrong in a new way: a mapping that names a state the symbol does not have.**
  Refused at save, by name, in the same way ADR-0024 refuses a component kind that does not exist.
  What cannot be checked is whether a *correct* mapping is the right one for the plant, and nothing
  short of a walk will say.

- **`ScreenRules` grows a fourth question.** It already answers "does this kind need a tag", "does it
  need text" and "may it carry text"; now it also answers "does it need states, and are they valid for
  this symbol". The Phase 8 lesson applies directly — a rule and every place that writes the same kind
  of data have to move together — and the seeder and the editor are both places that write these.

- **A symbol's states are configuration, so they are versioned with the screen and travel with it.**
  An author who changes a pump's mapping changes what every operator on the Site sees, immediately,
  exactly as ADR-0024's save does. The sentence under the Save button already says so.

- **Still not decided, and deliberately:** whether components nest; free positioning; pipe routing;
  a symbol library beyond the pump. All of those are the mimic work, and none of them is implied by
  this. Neither is scripting, which stays refused.

## Verified in review by

- Saving a `symbol` whose states name a shape the symbol does not have is **refused with the state
  named**, and the screen is unchanged.
- A `symbol` bound to a tag the caller cannot see renders as unreadable, asserted through the same path
  the renderer uses.
- A tag reading **Bad** resolves to a `bad` symbol **whatever the value says**, and one reading Stale
  resolves to `stale` — proved for a boolean tag holding `true`, which is the case that would otherwise
  draw a turning pump.
- A reading no rule matches, with no fallback, resolves to `unknown` and is drawn as such.
- The mapping is evaluated in order with the first match winning, and the fallback is used only when
  nothing matched.
- A `symbol` consumed through the client's own resolver is tested without a browser, as the rest of the
  resolver is.
