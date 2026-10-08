// What the pump symbol draws in each state — the same kind of test as `tile-layout.test.mjs` and for
// the same reason: a rule about how something LOOKS is a rule, and a screenshot cannot stop a later
// change from putting the defect back.
//
// **The defect these pin was found by a person looking at a running screen on 2026-10-07**, and no
// instrument in this repository could have found it. A Bad pump drew the four vanes — which sit on a
// `+` — underneath the two cross lines — which sit on an `X`. Together they made an **eight-pointed
// star**, so the crossed-out pump read as a *busier* pump rather than a cancelled one, and the only
// thing left separating "nothing is measuring this" from "the plant says it is off" was a change of
// colour. That is ADR-0003's distinction collapsing in the medium where it is hardest to notice.
//
// ADR-0027 §4 had already said what was wanted, in the component's own comment: a picture of a machine
// with no reading "is not drawn as a machine at all". It was. Now the rotor is not drawn in `bad`.
//
// Read from the source rather than from a built bundle, because the defect was in the source.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const symbol = readFileSync(new URL('../src/app/symbol.ts', import.meta.url), 'utf8');

/** The component's template, which is a template literal inside the decorator. */
const template = symbol.slice(symbol.indexOf('template: `'), symbol.indexOf('styles: `'));

/** The component's stylesheet, the literal after it. */
const styles = symbol.slice(symbol.indexOf('styles: `'));

/**
 * Why the nearest state guard before a marker is, or is not, the one that excludes bad.
 *
 * A scan rather than a regex over one drawing, because the template grew from one shape to four on
 * 2026-10-07 and a literal match pinned the old nesting instead of the rule. **The rule is that no
 * part of a machine is drawn when nothing is measuring it** — whichever machine it is.
 */
function notGuardedAgainstBad(marker) {
  const at = template.indexOf(marker);

  if (at === -1) {
    return `the template no longer contains ${marker}`;
  }

  const guards = [...template.slice(0, at).matchAll(/@if \(state\(\) [!=]== '(\w+)'\)/g)];
  const nearest = guards.at(-1);

  return nearest !== undefined && nearest[0] === "@if (state() !== 'bad')"
    ? null
    : `${marker} is not inside a guard against bad (nearest is ${nearest?.[0] ?? 'none'})`;
}

test('no part of a machine is drawn when the reading is Bad', () => {
  // The defect this pins: the vanes sit on a plus and the cross on a diagonal, so drawn together they
  // made an eight-pointed star, and the crossed-out pump read as a BUSIER pump. Every drawing added
  // since has to obey the same rule, which is why this walks all of them rather than the impeller.
  const parts = [
    '<g class="rotor"',
    '<polygon class="body vee"',
    '<rect class="fill"',
    '<text class="letter"',
  ];

  for (const marker of parts) {
    assert.equal(notGuardedAgainstBad(marker), null);
  }
});

test('the cross is still drawn when the reading is Bad', () => {
  // The other half, and it has to be asserted separately: removing the rotor without the cross would
  // leave a dashed empty circle, which says far less than a cancelled machine does.
  assert.match(template, /@if \(state\(\) === 'bad'\) \{/);
  assert.equal(
    (template.match(/<line class="cross"/g) ?? []).length,
    2,
    'a cross is two lines',
  );
});

test('the cross is heavier than a vane, because it is not part of the machine', () => {
  // Not decoration: the cross is the mark that cancels the drawing, and it is the only thing inside
  // the body once the rotor is gone. If it ever becomes the same weight as a vane it will read as
  // one again, which is how the original defect worked.
  const cross = Number(styles.match(/\.cross \{[^}]*stroke-width:\s*([\d.]+)/)?.[1]);
  const vane = Number(styles.match(/\.vanes line \{[^}]*stroke-width:\s*([\d.]+)/)?.[1]);

  assert.ok(Number.isFinite(cross), 'the cross has no stroke-width');
  assert.ok(Number.isFinite(vane), 'a vane has no stroke-width');
  assert.ok(cross > vane, `the cross (${cross}) must be heavier than a vane (${vane})`);
});

test('bad is the only state that hides anything, because every other one is equipment that exists', () => {
  // The control, and the reason it matters: `unknown` and `stale` are NOT "nothing is measuring
  // this". `stale` has a reading that should not be leaned on; `unknown` has a Good reading the
  // mapping does not cover (ADR-0027 §3). In both the equipment is there and its parts belong.
  //
  // A fix that hid them whenever the state was not `running` would pass the test above and be wrong
  // about both of those — and about a valve and a tank, which have no `running` at all.
  const guards = new Set(template.match(/@if \(state\(\) !== '(\w+)'\)/g) ?? []);

  assert.ok(guards.size > 0, 'nothing is guarded against anything');
  assert.deepEqual([...guards], ["@if (state() !== 'bad')"]);
});

test('only a machine that turns turns, and only when it is running', () => {
  // ADR-0027 §5, re-asserted here because this file owns what the drawing does: animation comes from
  // the state, and no state an author can name makes a stopped machine spin.
  //
  // **Asserted as the rule rather than as the expression it used to be written in.** On 2026-10-07 a
  // motor joined the pump, and a test matching the old line verbatim failed on a change it should
  // have allowed — while saying nothing at all about whether a valve or a tank had started moving.
  const animates = symbol.slice(symbol.indexOf('animates = computed('));
  const expression = animates.slice(0, animates.indexOf(';'));

  assert.match(expression, /=== 'running'/, 'animation is still tied to running');
  assert.doesNotMatch(expression, /'stopped'|'fault'|'unknown'|'stale'|'bad'/);
  assert.doesNotMatch(expression, /'valve'|'tank'/, 'a valve and a tank do not move');
  assert.match(expression, /'pump'/);
  assert.match(expression, /'motor'/);
});
