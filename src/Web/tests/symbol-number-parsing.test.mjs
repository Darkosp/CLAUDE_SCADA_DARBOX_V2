// Plain JavaScript and Node's own test runner, like the other tests here.
//
// What counts as a number in a symbol's mapping, and why the client must answer it the way Core does
// (ADR-0027).
//
// **ADR-0027 evaluates the same rules in two languages.** The argument for tolerating that duplication
// is written at `deriveSymbolState`, and it rests on one claim: *both sides are held to the same
// cases*. This file exists because that claim was false in three places, found on 2026-10-07 by reading
// the two parsers side by side and then **measuring both** rather than reasoning about them:
//
// | typed    | .NET `double.TryParse(…, Float, Invariant)` | JS `Number()` |
// |----------|---------------------------------------------|---------------|
// | `""`     | refused                                     | **0**         |
// | `"   "`  | refused                                     | **0**         |
// | `"0x10"` | refused                                     | **16**        |
//
// **The empty one was reachable, and that is what makes this a defect rather than a curiosity.** The
// editor's `addRule` creates a rule whose value is empty, and the live preview evaluates the draft at
// once — so an author adding a state to a symbol bound to a tag reading `0.00` watched the preview
// enter that state, and then the save was refused, because the API will not store a comparison with
// nothing to compare against. A preview exists to show what an operator will see; this one was showing
// a state the product cannot produce.
//
// `tests/Core.Tests/SymbolNumberParsingTests.cs` asserts the same table from the other side.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { deriveSymbolState } from '../.node-test/screen.js';

const numeric = (value) => ({ kind: 'numeric', numeric: value });
const reading = (value) => ({ value, quality: 'Good' });

/** `state` when the reading equals `typed`, and nothing otherwise — no fallback, so a miss is `unknown`. */
const equals = (typed) => [{ state: 'running', when: 'equals', value: typed, otherwise: false }];

/** `state` when the reading is above `typed`. */
const above = (typed) => [{ state: 'running', when: 'above', value: typed, otherwise: false }];

test('an empty comparison matches nothing, and a reading of zero least of all', () => {
  // The defect. `Number('')` is 0 and finite, so this used to answer `running` for a tag reading 0.00
  // in the editor's live preview, for a rule the author had not finished typing.
  assert.equal(deriveSymbolState(equals(''), reading(numeric(0))).state, 'unknown');
  assert.equal(deriveSymbolState(equals('   '), reading(numeric(0))).state, 'unknown');
});

test('an empty threshold is not zero either', () => {
  assert.equal(deriveSymbolState(above(''), reading(numeric(5))).state, 'unknown');
});

test('a hexadecimal literal is not a number here, because it is not one in Core', () => {
  // `Number('0x10')` is 16; .NET refuses it. The product's rule is .NET's, because that is what the
  // API validates against and what a refusal quotes back.
  assert.equal(deriveSymbolState(equals('0x10'), reading(numeric(16))).state, 'unknown');
  assert.equal(deriveSymbolState(above('0x10'), reading(numeric(99))).state, 'unknown');
});

test('a decimal comma is not a decimal point, which is the case this project will meet', () => {
  // A Macedonian keyboard produces `1,5`. `parseNumberField` converts it for the alarm threshold form;
  // a symbol's mapping stores what the author typed and parses it invariantly, so here it is refused.
  // That difference between two forms of this same client is recorded as a question in open-work §3 —
  // this test pins what the code does today, not an argument that it is the right answer.
  assert.equal(deriveSymbolState(equals('1,5'), reading(numeric(1.5))).state, 'unknown');
});

test('the forms .NET does accept are accepted, which is the control', () => {
  // Without these, a parser that refused everything would pass every test above.
  assert.equal(deriveSymbolState(equals('4.50'), reading(numeric(4.5))).state, 'running');
  assert.equal(deriveSymbolState(equals(' 12 '), reading(numeric(12))).state, 'running');
  assert.equal(deriveSymbolState(equals('1e3'), reading(numeric(1000))).state, 'running');
  assert.equal(deriveSymbolState(equals('-273.15'), reading(numeric(-273.15))).state, 'running');
  assert.equal(deriveSymbolState(equals('.5'), reading(numeric(0.5))).state, 'running');
  assert.equal(deriveSymbolState(above('0'), reading(numeric(0.01))).state, 'running');
});

test('a non-finite threshold matches nothing, on this side as on the other', () => {
  // .NET parses `Infinity` and `NaN`, so without an explicit guard `below Infinity` would match every
  // numeric reading in Core while matching none here — a rule alive in the tests and dead in the
  // product. Both sides now refuse it, and `ScreenRules` refuses storing it at all.
  const below = (typed) => [{ state: 'running', when: 'below', value: typed, otherwise: false }];

  assert.equal(deriveSymbolState(below('Infinity'), reading(numeric(5))).state, 'unknown');
  assert.equal(deriveSymbolState(above('-Infinity'), reading(numeric(5))).state, 'unknown');
  assert.equal(deriveSymbolState(above('NaN'), reading(numeric(5))).state, 'unknown');
});

test('NaN equals nothing, including a reading that is itself NaN', () => {
  // .NET: `double.NaN == double.NaN` is false. JavaScript agrees. Pinned because the parser now
  // returns NaN rather than refusing it, and a parser that returned 0 instead would pass silently.
  assert.equal(deriveSymbolState(equals('NaN'), reading(numeric(Number.NaN))).state, 'unknown');
});
