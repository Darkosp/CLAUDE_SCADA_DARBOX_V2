// Plain JavaScript and Node's own test runner, like the other tests here.
//
// Whether a symbol's mapping fits the tag it is bound to (ADR-0027).
//
// **This file exists because of one defect, and the first test is that defect.** The walk of
// 2026-10-06 watched an author bind a numeric setpoint to a new pump symbol. The symbol was born with
// `running when the value is true` and `stopped otherwise`; a number never compares equal to `true`, so
// every reading fell through to the fallback and the symbol drew `stopped` for ever. Nothing refused
// it, nothing warned, and nothing looked wrong — **a fallback that is silently always taken looks like
// a working symbol reporting a stopped machine**, which is a claim about a plant that nothing measured.
//
// Two halves are tested here, and they are separate on purpose:
//
//   1. `unmatchableRules` — which rules can never match, so the editor can say so. It catches an author
//      who re-binds an existing symbol, which no default can.
//   2. `newComponent`'s starting mapping, which now follows the bound tag's kind, so the common way of
//      making the mistake does not arise.
//
// **The last two tests are the ones that keep this honest**: this must not start judging whether a
// well-formed mapping is the RIGHT one for the plant. `running when above 100` on a tag that never
// reads above 5 is a mapping only a walk can fault, and a warning that fired on it would teach an
// author to ignore warnings.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { deriveSymbolState, newComponent, unmatchableRules } from '../.node-test/screen.js';

/** What a new symbol used to be born with, for every tag, which is the defect. */
const ASSUMES_BOOLEAN = [
  { state: 'running', when: 'equals', value: 'true', otherwise: false },
  { state: 'stopped', when: null, value: null, otherwise: true },
];

const reading = (value, quality = 'Good') => ({ value, quality });

test('the defect: a boolean mapping on a numeric tag can never match, and is named', () => {
  const dead = unmatchableRules(ASSUMES_BOOLEAN, 'numeric');

  assert.equal(dead.length, 1, 'the one comparison is dead; the fallback is not');
  assert.equal(dead[0].at, 0, 'and the author is told which rule it is');
  assert.match(dead[0].reason, /number/);

  // The half that makes it dangerous rather than merely wrong: the symbol still draws something, and
  // what it draws is a definite claim about a machine.
  const drawn = deriveSymbolState(ASSUMES_BOOLEAN, reading({ kind: 'numeric', numeric: 42 }));
  assert.equal(drawn.state, 'stopped');
});

test('the same mapping on the boolean tag it was written for is not faulted', () => {
  // The control. A warning that also fired here would be noise, and the default below is this mapping.
  assert.deepEqual(unmatchableRules(ASSUMES_BOOLEAN, 'boolean'), []);
});

test('a fallback is never reported, whatever the tag reads', () => {
  // A fallback matches anything by definition. Whether it is the right thing to draw when nothing
  // matched is the author's decision and ADR-0027 §3 leaves it to them.
  const onlyAFallback = [{ state: 'unknown', when: null, value: null, otherwise: true }];

  for (const kind of ['numeric', 'boolean', 'text', 'discrete', 'none']) {
    assert.deepEqual(unmatchableRules(onlyAFallback, kind), [], `a fallback is live for ${kind}`);
  }
});

test('a tag nothing has measured yet produces no warnings at all', () => {
  // `none` means nothing is known, not that nothing matches. Guessing here would warn about a mapping
  // that is very likely right, which is how a reader learns to ignore a warning.
  assert.deepEqual(unmatchableRules(ASSUMES_BOOLEAN, 'none'), []);
});

test('an ordering comparison is dead on a tag that has no ordering', () => {
  const above = [{ state: 'running', when: 'above', value: '0', otherwise: false }];

  assert.equal(unmatchableRules(above, 'boolean').length, 1);
  assert.equal(unmatchableRules(above, 'text').length, 1);
  assert.deepEqual(unmatchableRules(above, 'numeric'), [], 'and live on one that does');
});

test('an ordering comparison against something that is not a number is dead', () => {
  // The server refuses this one at save (`ScreenRules`), because it needs no tag to decide. It is
  // checked here as well because the editor shows it while the author is typing, which is before any
  // save can refuse anything.
  const nonsense = [{ state: 'running', when: 'above', value: 'hot', otherwise: false }];

  assert.equal(unmatchableRules(nonsense, 'numeric').length, 1);
});

test('an equality that cannot be the kind the tag reports is dead', () => {
  const equalsTrue = [{ state: 'running', when: 'equals', value: 'true', otherwise: false }];
  const equalsOne = [{ state: 'running', when: 'equals', value: '1', otherwise: false }];

  assert.equal(unmatchableRules(equalsTrue, 'numeric').length, 1, "'true' is not a number");
  assert.equal(unmatchableRules(equalsOne, 'boolean').length, 1, "'1' is not true or false");
  assert.deepEqual(unmatchableRules(equalsOne, 'numeric'), []);
  assert.deepEqual(unmatchableRules(equalsTrue, 'boolean'), []);
});

test('any text is something a text tag could equal, so nothing is faulted', () => {
  const anything = [
    { state: 'running', when: 'equals', value: 'RUN', otherwise: false },
    { state: 'fault', when: 'equals', value: '0', otherwise: false },
  ];

  assert.deepEqual(unmatchableRules(anything, 'text'), []);
});

test('every comparison is dead on a discrete tag, because none of them can read one', () => {
  // `equalsValue` has no arm for a discrete reading, so this is not a judgement — it is what the
  // evaluator does. Said plainly here rather than left for an author to infer from a symbol that never
  // changes, and it is the same gap as `discrete` having no editor.
  const dead = unmatchableRules(ASSUMES_BOOLEAN, 'discrete');

  assert.equal(dead.length, 1);
  assert.match(dead[0].reason, /discrete/);
});

test('a new symbol on a numeric tag is born with a mapping that can match it', () => {
  const component = newComponent('symbol', 'tag-1', null, 'numeric');

  assert.equal(component.symbol, 'pump');
  assert.deepEqual(unmatchableRules(component.states, 'numeric'), []);

  // Not merely "not dead" — it has to derive both states from real readings, or the default would be
  // one that passes this file and still says nothing on a screen.
  assert.equal(deriveSymbolState(component.states, reading({ kind: 'numeric', numeric: 7 })).state, 'running');
  assert.equal(deriveSymbolState(component.states, reading({ kind: 'numeric', numeric: 0 })).state, 'stopped');
});

test('a new symbol on a boolean tag keeps the mapping most pumps want', () => {
  const component = newComponent('symbol', 'tag-1', null, 'boolean');

  assert.deepEqual(unmatchableRules(component.states, 'boolean'), []);
  assert.equal(deriveSymbolState(component.states, reading({ kind: 'boolean', boolean: true })).state, 'running');
  assert.equal(deriveSymbolState(component.states, reading({ kind: 'boolean', boolean: false })).state, 'stopped');
});

test('a new symbol on a tag nothing has measured still gets the boolean mapping', () => {
  // A deliberate bet, argued at `defaultSymbolStates`: most pumps are driven by a run signal, the
  // author is looking at the mapping, and the warning arrives as soon as a reading disagrees.
  const component = newComponent('symbol', 'tag-1', null);

  assert.deepEqual(component.states, ASSUMES_BOOLEAN);
});

test('a new symbol on a text or discrete tag admits it is not mapped yet', () => {
  // There is nothing to guess, and a guess here would be the defect again in another kind. A lone
  // `unknown` fallback is saveable, draws ADR-0027 §3's own "the mapping does not cover this" state,
  // and is not a mapping pretending to work.
  for (const kind of ['text', 'discrete']) {
    const component = newComponent('symbol', 'tag-1', null, kind);

    assert.equal(component.states.length, 1, `${kind}: one rule`);
    assert.equal(component.states[0].otherwise, true, `${kind}: and it is the fallback`);
    assert.equal(component.states[0].state, 'unknown');
    assert.deepEqual(unmatchableRules(component.states, kind), []);
  }
});

test('no kind of tag gives a non-symbol component any states', () => {
  for (const kind of ['label', 'value', 'trend', 'alarms', 'status']) {
    assert.deepEqual(newComponent(kind, null, 'x', 'numeric').states, [], kind);
    assert.equal(newComponent(kind, null, 'x', 'numeric').symbol, null, kind);
  }
});

test('a mapping that is merely unlikely is not faulted, and this is the line', () => {
  // `running when above 100` on a tag that in practice never exceeds 5 is wrong, and no code can know
  // it: the threshold is a number, the tag reports numbers, and the rule could match tomorrow. This is
  // the boundary `ScreenRules` draws in words — "whether a well-formed mapping is the right one for a
  // plant ... is a walk's job" — and a test is the only thing that keeps a boundary where it was put.
  const unlikely = [
    { state: 'running', when: 'above', value: '100', otherwise: false },
    { state: 'stopped', when: null, value: null, otherwise: true },
  ];

  assert.deepEqual(unmatchableRules(unlikely, 'numeric'), []);
});

test('a rule that reads a different state of the same tag is not faulted either', () => {
  // Two rules on one numeric tag, one of which can never fire for a given reading. "Can never match
  // THIS reading" is not the question — only "can never match ANY reading of this kind" is, because the
  // first is what a mapping is for.
  const band = [
    { state: 'fault', when: 'above', value: '90', otherwise: false },
    { state: 'running', when: 'above', value: '0', otherwise: false },
    { state: 'stopped', when: null, value: null, otherwise: true },
  ];

  assert.deepEqual(unmatchableRules(band, 'numeric'), []);
});
