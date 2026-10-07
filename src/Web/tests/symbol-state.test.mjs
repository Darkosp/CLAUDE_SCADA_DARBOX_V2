// Plain JavaScript and Node's own test runner, like the other tests here.
//
// Deriving a symbol's state from a reading, in the client (ADR-0027).
//
// **The same rules are evaluated in Core's `SymbolStates` and tested there too, and these tests exist
// because the duplication is real.** The two implementations are in two languages and cannot share
// code; what keeps them honest is that the SHAPE of a mapping is fixed by the API — the server refuses
// a comparison it does not evaluate — and that both sides are held to the same cases.
//
// So this file deliberately mirrors `tests/Core.Tests/SymbolStateTests.cs` case for case, including
// the one that matters most: **quality wins over the value.** If a future change makes one side behave
// differently, one of the two files will say so.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { deriveSymbolState, newComponent, resolveComponent } from '../.node-test/screen.js';

/** A pump's mapping, as an author would build it. */
const PUMP = [
  { state: 'running', when: 'equals', value: 'true', otherwise: false },
  { state: 'fault', when: 'equals', value: 'FAULT', otherwise: false },
  { state: 'stopped', when: 'equals', value: 'false', otherwise: true },
];

const numeric = (value) => ({ kind: 'numeric', numeric: value });
const boolean = (value) => ({ kind: 'boolean', boolean: value });
const text = (value) => ({ kind: 'text', text: value });

const reading = (value, quality = 'Good') => ({ value, quality });

test('a reading matching a rule draws that rule state', () => {
  const derived = deriveSymbolState(PUMP, reading(boolean(true)));

  assert.equal(derived.state, 'running');
  assert.equal(derived.fromQuality, false);
  assert.equal(derived.matchedRule, 0);
});

test('the first matching rule wins even when a later one also matches', () => {
  // Two rules that both match, deliberately: the second is dead and must stay dead. Order is meaning
  // in this list (ADR-0027 §2), so a list evaluated any other way would make an author's arrangement
  // of it decorative.
  const mapping = [
    { state: 'running', when: 'equals', value: 'true', otherwise: false },
    { state: 'fault', when: 'equals', value: 'true', otherwise: false },
  ];

  assert.equal(deriveSymbolState(mapping, reading(boolean(true))).state, 'running');
});

test('a fallback matches anything and is used only when nothing else did', () => {
  // "Anything" is the point: the fallback would match the fault reading too, and must not, because it
  // is listed after it.
  const fault = deriveSymbolState(PUMP, reading(text('FAULT')));

  assert.equal(fault.state, 'fault');
  assert.equal(fault.matchedRule, 1);

  const neither = deriveSymbolState(PUMP, reading(text('SOMETHING ELSE')));

  assert.equal(neither.state, 'stopped');
  assert.equal(neither.matchedRule, 2);
});

test('a reading no rule matches and no fallback resolves to unknown', () => {
  // Not to any of the mapped states. Falling through to one would be inventing a fact about a plant,
  // and `unknown` is drawn rather than silent so the author can see the gap.
  const noFallback = [{ state: 'running', when: 'equals', value: 'true', otherwise: false }];

  const derived = deriveSymbolState(noFallback, reading(boolean(false)));

  assert.equal(derived.state, 'unknown');
  assert.equal(derived.matchedRule, null);
});

test('a reading that is not Good takes its state from the quality and not from the mapping', () => {
  // **The test that matters, and the reason it reads `true`.** The tag is reading true — which the
  // mapping turns into a running pump — and the quality says that reading cannot be trusted. A pump
  // that is running is a claim about the world, and this refuses to make it on data nothing measured
  // (ADR-0003, ADR-0027 §4).
  for (const [quality, expected] of [
    ['Bad', 'bad'],
    ['Stale', 'stale'],
    ['Uncertain', 'stale'],
  ]) {
    const derived = deriveSymbolState(PUMP, reading(boolean(true), quality));

    assert.equal(derived.state, expected, `${quality} should draw ${expected}`);
    assert.equal(derived.fromQuality, true);
    assert.equal(derived.matchedRule, null, 'the mapping must not have been consulted');
  }
});

test('a tag nothing has ever arrived for is unknown rather than a state', () => {
  assert.equal(deriveSymbolState(PUMP, null).state, 'unknown');
});

test('an empty mapping resolves to unknown, which is what a new symbol with no rules looks like', () => {
  assert.equal(deriveSymbolState([], reading(boolean(true))).state, 'unknown');
});

test('a threshold compares a number, and is exclusive at the boundary', () => {
  // `above 10` does not match 10. Stated because either choice is defensible and only one can be
  // true, and an author setting a limit on a plant needs to know which.
  const above = [{ state: 'high', when: 'above', value: '10', otherwise: false }];

  assert.equal(deriveSymbolState(above, reading(numeric(10))).state, 'unknown');
  assert.equal(deriveSymbolState(above, reading(numeric(10.0001))).state, 'high');

  const below = [{ state: 'low', when: 'below', value: '10', otherwise: false }];

  assert.equal(deriveSymbolState(below, reading(numeric(9.9999))).state, 'low');
  assert.equal(deriveSymbolState(below, reading(numeric(10))).state, 'unknown');
});

test('a threshold does not match a boolean, because a boolean has no ordering', () => {
  const above = [{ state: 'running', when: 'above', value: '0', otherwise: false }];

  assert.equal(deriveSymbolState(above, reading(boolean(true))).state, 'unknown');
});

test('equality parses, so a trailing zero is the same threshold and True is the same boolean', () => {
  // An author lining a form up must not thereby create a state nothing reaches.
  const fourFifty = [{ state: 'at', when: 'equals', value: '4.50', otherwise: false }];

  assert.equal(deriveSymbolState(fourFifty, reading(numeric(4.5))).state, 'at');

  const capitalised = [{ state: 'running', when: 'equals', value: 'True', otherwise: false }];

  assert.equal(deriveSymbolState(capitalised, reading(boolean(true))).state, 'running');
});

test('a comparison this build does not know matches nothing rather than throwing', () => {
  // The API refuses an unknown comparison at save time. This is the second line: the mapping is data,
  // a screen is read by everyone, and a rule that cannot be evaluated should cost its own state rather
  // than the screen.
  const mapping = [
    { state: 'running', when: 'approximately', value: 'true', otherwise: false },
    { state: 'stopped', when: 'equals', value: 'false', otherwise: true },
  ];

  assert.equal(deriveSymbolState(mapping, reading(boolean(true))).state, 'stopped');
});

// ---- through the resolver, which is where a screen actually reaches this ----

const SITE = 'site-1';

function snapshot(tagId, value, quality = 'Good') {
  return {
    tagId,
    path: 'Skopje/Pump House/Pump Running',
    value,
    sourceTimestampUtc: '2026-10-06T12:00:00Z',
    quality,
    unitSymbol: null,
  };
}

function pumpComponent() {
  const component = newComponent('symbol', 'tag-1', null);

  // A new symbol is born as a pump with one obvious rule and an editable fallback (see
  // `newComponent`), so this is what an author gets without touching anything.
  return component;
}

test('a new symbol is born as a pump with a mapping, so it can be saved at once', () => {
  // The API refuses a symbol with no states. "Start empty and let the author fill it in" would hand
  // them a component that cannot be saved until they have understood the mapping.
  const component = pumpComponent();

  assert.equal(component.symbol, 'pump');
  assert.ok(component.states.length > 0, 'a new symbol must have a mapping');
  assert.ok(component.states.some((rule) => rule.otherwise), 'and a fallback, so nothing is unknown');
});

test('a symbol resolves to the state its mapping derives, not to the reading as text', () => {
  const resolved = resolveComponent(
    pumpComponent(),
    new Map([['tag-1', snapshot('tag-1', boolean(true))]]),
    [],
    SITE,
  );

  assert.equal(resolved.kind, 'symbol');
  assert.equal(resolved.shape, 'pump');
  assert.equal(resolved.state, 'running');
  assert.equal(resolved.fromQuality, false);
});

test('a symbol whose tag has gone Bad draws bad and says the quality, through the resolver', () => {
  // The whole point, taken through the path the renderer actually uses rather than by calling the
  // derivation directly: a boolean tag holding `true` and a Bad quality must not draw a turning pump.
  const resolved = resolveComponent(
    pumpComponent(),
    new Map([['tag-1', snapshot('tag-1', boolean(true), 'Bad')]]),
    [],
    SITE,
  );

  assert.equal(resolved.kind, 'symbol');
  assert.equal(resolved.state, 'bad');
  assert.equal(resolved.fromQuality, true);
});

test('a symbol the reader may not see resolves as unreadable, like every other binding', () => {
  const component = { ...pumpComponent(), readable: false };

  const resolved = resolveComponent(component, new Map(), [], SITE);

  assert.equal(resolved.kind, 'unreadable');
});

test('a symbol whose tag has never reported is missing rather than a pump at rest', () => {
  // Distinct from Bad: a Bad tag HAS a reading and it is not good. This one has nothing, and
  // "no reading" is what the screen must say rather than drawing a stopped machine.
  const resolved = resolveComponent(pumpComponent(), new Map(), [], SITE);

  assert.equal(resolved.kind, 'missing');
});

test('a symbol this build cannot draw resolves as missing rather than as a blank tile', () => {
  // The server refuses an unknown shape at save time, so reaching here means the row was written
  // another way -- and a blank tile is the outcome ADR-0024 exists to prevent.
  //
  // **This said `tank` until 2026-10-07, when a tank became a shape this build draws** and the test
  // started failing on its own premise rather than on its subject. A plausible name is the wrong
  // choice for "something that does not exist": the vocabulary grows, and ADR-0027 §6 says it is
  // meant to. `turbine` is not on anyone's list of next symbols.
  const component = { ...pumpComponent(), symbol: 'turbine' };

  const resolved = resolveComponent(
    component,
    new Map([['tag-1', snapshot('tag-1', boolean(true))]]),
    [],
    SITE,
  );

  assert.equal(resolved.kind, 'missing');
});
