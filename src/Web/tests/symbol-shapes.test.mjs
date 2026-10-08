// Four symbols instead of one, and what that costs (ADR-0027 §6).
//
// ADR-0027 shipped a pump and said what a second shape would cost: *"a drawing and a state list, not a
// decision"*, naming a tank, a valve and a motor. These are those three. The claim held — no ADR, no
// schema change, no new API field — but it was not free, and this file is the bill:
//
//   1. **a symbol is a drawing plus a state list, and the list belongs to the drawing.** Changing a
//      pump into a valve leaves rules naming `running` and `stopped`, which a valve cannot be drawn
//      in, so the server refuses the save by name about rules the author never wrote;
//   2. **a starting mapping has to fit the shape as well as the tag.** The 2026-10-07 fix made the
//      default follow the tag's kind; a valve needs `open`/`closed` and a tank needs bands.
//
// Both are the same shape of defect this project has found in four walks: a rule and every place that
// applies it have to move together.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  SYMBOL_STATES,
  deriveSymbolState,
  newComponent,
  remapForShape,
  unmatchableRules,
} from '../.node-test/screen.js';

const reading = (value, quality = 'Good') => ({ value, quality });
const numeric = (value) => ({ kind: 'numeric', numeric: value });
const boolean = (value) => ({ kind: 'boolean', boolean: value });

const shapes = Object.keys(SYMBOL_STATES);

test('this build draws four shapes, and every one can show the three states quality forces', () => {
  // The rule that makes the set safe. ADR-0027 §4: a reading that is not Good produces `bad` or
  // `stale` whatever it says, and a mapping with no match produces `unknown`. A shape missing one of
  // those would have a state the engine can reach and the drawing cannot show.
  assert.deepEqual(shapes, ['pump', 'motor', 'valve', 'tank']);

  for (const shape of shapes) {
    for (const forced of ['unknown', 'bad', 'stale']) {
      assert.ok(SYMBOL_STATES[shape].includes(forced), `${shape} cannot draw ${forced}`);
    }
  }
});

test('each shape has its own plant states, and they are not interchangeable', () => {
  // A valve is not running and a tank is not stopped. The states are the drawing's, which is why a
  // mapping is only valid against a particular shape.
  assert.ok(SYMBOL_STATES.valve.includes('open'));
  assert.ok(!SYMBOL_STATES.pump.includes('open'));
  assert.ok(SYMBOL_STATES.tank.includes('high'));
  assert.ok(!SYMBOL_STATES.valve.includes('high'));
});

test('changing a shape keeps a mapping every one of whose states survives', () => {
  // A pump and a motor are the same six states, so an author who has tuned a threshold keeps it.
  // Replacing here would throw away work for nothing.
  const tuned = [
    { state: 'running', when: 'above', value: '37.5', otherwise: false },
    { state: 'stopped', when: null, value: null, otherwise: true },
  ];

  assert.deepEqual(remapForShape(tuned, 'motor', 'numeric'), tuned);
});

test('changing to a shape that cannot draw the old states replaces the mapping', () => {
  // **The defect this prevents.** Left alone, a pump turned into a valve carries `running` and
  // `stopped`, which `Symbols.CanDraw` refuses — so the save comes back naming rules the author never
  // wrote, pointing at a component they only wanted to re-shape.
  const pumpRules = [
    { state: 'running', when: 'equals', value: 'true', otherwise: false },
    { state: 'stopped', when: null, value: null, otherwise: true },
  ];

  const asValve = remapForShape(pumpRules, 'valve', 'boolean');

  for (const rule of asValve) {
    assert.ok(SYMBOL_STATES.valve.includes(rule.state), `a valve cannot be drawn as ${rule.state}`);
  }

  // And it is a mapping that works, not merely one that saves.
  assert.equal(deriveSymbolState(asValve, reading(boolean(true))).state, 'open');
  assert.equal(deriveSymbolState(asValve, reading(boolean(false))).state, 'closed');
});

test('every shape is born with a mapping that can match the tag it is bound to', () => {
  // The 2026-10-07 rule, carried to four shapes: a default that cannot match is a symbol that draws
  // its fallback for ever while looking configured.
  for (const shape of shapes) {
    for (const kind of ['numeric', 'boolean']) {
      const states = remapForShape([{ state: 'nothing-real', when: null, value: null, otherwise: true }], shape, kind);

      assert.deepEqual(
        unmatchableRules(states, kind),
        [],
        `${shape} bound to a ${kind} tag starts with a rule that can never match`,
      );

      for (const rule of states) {
        assert.ok(SYMBOL_STATES[shape].includes(rule.state), `${shape} cannot draw ${rule.state}`);
      }
    }
  }
});

test('a tank bound to a numeric tag reads its bands, and they are the author’s to move', () => {
  // Bands, never a fill that tracks the reading (ADR-0027 §5). Twenty and eighty are where a reader
  // expects "nearly empty" and "nearly full" on a percentage; on a tag in metres they are wrong and
  // VISIBLY wrong, which is the point — a default that looked plausible on every unit is one nobody
  // would check.
  const tank = remapForShape([{ state: 'running', when: null, value: null, otherwise: true }], 'tank', 'numeric');

  assert.equal(deriveSymbolState(tank, reading(numeric(95))).state, 'high');
  assert.equal(deriveSymbolState(tank, reading(numeric(50))).state, 'normal');
  assert.equal(deriveSymbolState(tank, reading(numeric(5))).state, 'low');
});

test('a tank bound to a tag that is not a number admits it rather than inventing a band', () => {
  // A level is a quantity. A boolean tells a tank nothing, and a default that picked a band from one
  // would be the product claiming to know something it does not.
  const tank = remapForShape([{ state: 'running', when: null, value: null, otherwise: true }], 'tank', 'boolean');

  assert.equal(tank.length, 1);
  assert.equal(tank[0].state, 'unknown');
  assert.equal(tank[0].otherwise, true);
});

test('a new symbol still starts as a pump, so nothing an author built changes shape by itself', () => {
  // The picker offers four now; the one you get without choosing is the one that was there before.
  const component = newComponent('symbol', 'tag-1', null, 'boolean');

  assert.equal(component.symbol, 'pump');
  assert.equal(deriveSymbolState(component.states, reading(boolean(true))).state, 'running');
});

test('quality still overrides every shape, which is the rule four drawings must not weaken', () => {
  // ADR-0027 §4. The temptation with more shapes is a per-shape answer; there is one answer, and it
  // comes before the mapping is consulted at all.
  for (const shape of shapes) {
    const states = remapForShape([{ state: 'x', when: null, value: null, otherwise: true }], shape, 'numeric');

    assert.equal(deriveSymbolState(states, reading(numeric(99), 'Bad')).state, 'bad');
    assert.equal(deriveSymbolState(states, reading(numeric(99), 'Stale')).state, 'stale');
    assert.equal(deriveSymbolState(states, null).state, 'unknown');
  }
});
