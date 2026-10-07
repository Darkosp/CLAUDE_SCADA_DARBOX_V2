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

test('the rotor is not drawn when the reading is Bad', () => {
  // The defect itself. The rotor must be inside a guard that excludes `bad`, so that the cross has
  // nothing to be confused with.
  const guard = template.match(/@if \(state\(\) !== 'bad'\) \{\s*\n\s*<g class="rotor"/);

  assert.ok(
    guard,
    'the rotor group is no longer guarded against `bad`: a crossed-out pump will draw its vanes '
      + 'under the cross again, and the two read as an eight-pointed star rather than a cancellation',
  );
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

test('no state but `bad` hides the rotor, because every other one is a machine that exists', () => {
  // The control, and the reason it matters: `unknown` and `stale` are NOT "nothing is measuring
  // this". `stale` has a reading that should not be leaned on; `unknown` has a Good reading the
  // mapping does not cover (ADR-0027 §3). In both the machine is there and the rotor belongs.
  // A fix that hid the rotor whenever the state was not `running` would pass the first test here and
  // be wrong about both of those.
  const guards = template.match(/@if \(state\(\) !== '(\w+)'\)/g) ?? [];

  assert.deepEqual(guards, ["@if (state() !== 'bad')"]);
});

test('a running pump is the only one that turns, and that is still true', () => {
  // Carried from ADR-0027 §5 and re-asserted here because this file now owns what the drawing does:
  // animation comes from the state, and no state an author can name makes a stopped pump spin.
  assert.match(
    symbol,
    /animates = computed\(\(\) => this\.shape\(\) === 'pump' && this\.state\(\) === 'running'\)/,
  );
});
