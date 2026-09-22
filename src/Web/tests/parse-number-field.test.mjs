// Plain JavaScript on purpose. The test runner is Node's own (`node:test`, built in
// since Node 18) and the assertions are `node:assert` — neither is a dependency, which
// ADR-0006 would require an amendment to add. `@types/node` is not installed either, so
// a TypeScript test importing `node:test` would not type-check; this imports the
// compiled `models.js` that `npm test` produces with the TypeScript compiler the project
// already has.
//
// What is under test is a defect the compiler cannot see, found by hand at step 5 of the
// Phase 5.5 gate: an `<input type="number">` bound with ngModel hands over a *number*,
// the draft declared the field a `string`, and `raw.trim()` threw on the first save.
// Type-checking passed throughout, because it checks the declaration and not what the
// binding does at runtime. Only running the code catches this.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { parseNumberField } from '../.node-test/models.js';

/** [name, input, expected] — expected is {ok:false} or {ok:true, value}. */
const cases = [
  ['null is an empty field', null, { ok: true, value: null }],
  ['undefined is an empty field', undefined, { ok: true, value: null }],
  ['an empty string is an empty field', '', { ok: true, value: null }],
  ['whitespace alone is an empty field', '   ', { ok: true, value: null }],

  // The defect itself: type="number" + ngModel hands over a number, not a string.
  ['a number, as type="number" supplies it', 3, { ok: true, value: 3 }],
  ['a negative number', -2.5, { ok: true, value: -2.5 }],

  // Blank and zero mean opposite things for a limit, and must never collapse together.
  ['zero stays zero and does not become blank', 0, { ok: true, value: 0 }],
  ['zero as text stays zero', '0', { ok: true, value: 0 }],

  ['a decimal point', '3.5', { ok: true, value: 3.5 }],
  ['a decimal comma', '3,5', { ok: true, value: 3.5 }],

  // Refused, not passed on as NaN: JSON.stringify turns NaN into null, which the
  // Gateway reads as "no limit on this side" — a silently removed threshold.
  ['text is refused rather than becoming "no limit"', 'abc', { ok: false }],
  ['NaN is refused', Number.NaN, { ok: false }],
  ['Infinity is refused', Infinity, { ok: false }],
  ['a thousands separator is refused, not guessed at', '1,234.5', { ok: false }],
];

for (const [name, input, expected] of cases) {
  test(name, () => {
    const actual = parseNumberField(input);

    assert.equal(actual.ok, expected.ok, `ok: ${JSON.stringify(actual)}`);
    if (expected.ok) {
      assert.equal(actual.value, expected.value, `value: ${JSON.stringify(actual)}`);
    }
  });
}

test('every case above is covered', () => {
  assert.equal(cases.length, 14);
});
