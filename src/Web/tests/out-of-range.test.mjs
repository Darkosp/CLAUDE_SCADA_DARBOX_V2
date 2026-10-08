// What a screen says about a reading outside the span its tag declares (ADR-0030 §5).
//
// **The negative cases are the point of this file.** ADR-0030 §3 is that *nothing declared says
// nothing* — a tag with no range gets no verdict, because a deployment that declared nothing must not
// be shown one nobody made. That is the same distinction ADR-0025 drew for a null on-delay: **null is
// not zero**, and here **absent is not "in range"**.
//
// So three of the six tests below assert silence. A marker that appeared on every tile would be noise;
// one that appeared on a tag with no range would be a lie.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { outOfRangeNote } from '../.node-test/tag.js';

test('a reading above its range says so, and says what it is above', () => {
  // "What it was compared with" is half the message: an operator who sees "above" without the number
  // has to go and look up the range before they can judge whether it matters.
  assert.equal(
    outOfRangeNote({ rangeStatus: 'AboveRange', rangeLow: 0, rangeHigh: 100 }),
    'above 100',
  );
});

test('a reading below its range says so, against the other end', () => {
  assert.equal(
    outOfRangeNote({ rangeStatus: 'BelowRange', rangeLow: -5, rangeHigh: 250 }),
    'below -5',
  );
});

test('a reading inside its range says NOTHING', () => {
  // The common case, and the one that would ruin a screen if it spoke. Every value tile on every
  // screen would carry a badge saying everything is fine, which is how a reader learns to stop
  // reading badges.
  assert.equal(outOfRangeNote({ rangeStatus: 'InRange', rangeLow: 0, rangeHigh: 100 }), null);
});

test('a tag that declared no range says nothing, which is not the same as being in range', () => {
  // ADR-0030 §3. The server sends a null status for "nothing declared" and for "nothing measured", and
  // both must read as silence rather than as a verdict.
  assert.equal(outOfRangeNote({}), null);
  assert.equal(outOfRangeNote({ rangeStatus: null }), null);
  assert.equal(outOfRangeNote({ rangeStatus: null, rangeLow: null, rangeHigh: null }), null);
});

test('a status this build does not know says nothing rather than guessing', () => {
  // A newer server could send a verdict this client has not been taught. Drawing it as "out of range"
  // would be inventing a reading's meaning; saying nothing loses a marker and invents nothing.
  assert.equal(outOfRangeNote({ rangeStatus: 'Sideways', rangeLow: 0, rangeHigh: 1 }), null);
});

test('a verdict whose end is missing still says which side, because the side is the fact', () => {
  // Defensive, and it has to go this way round: the side is what an operator acts on, and dropping the
  // whole marker because a number is absent would hide the one thing that matters.
  assert.equal(outOfRangeNote({ rangeStatus: 'AboveRange' }), 'above its declared range');
  assert.equal(outOfRangeNote({ rangeStatus: 'BelowRange', rangeLow: null }), 'below its declared range');
});

test('an end is written the way the reading beside it is written', () => {
  // `above 100.00` beside `104.30` reads as one scale. `above 1e2` does not, and nor does a float
  // printed with seventeen digits -- the arithmetic showing through, which `formatMeasurement` exists
  // to stop elsewhere.
  assert.equal(outOfRangeNote({ rangeStatus: 'AboveRange', rangeHigh: 4.5 }), 'above 4.50');
  assert.equal(outOfRangeNote({ rangeStatus: 'AboveRange', rangeHigh: 100 }), 'above 100');
  assert.equal(outOfRangeNote({ rangeStatus: 'BelowRange', rangeLow: 0 }), 'below 0');
});

test('zero is a real end, not a missing one', () => {
  // The trap in writing this: `rangeLow || 'its declared range'` would turn a perfectly good zero into
  // the vague form, and zero is the most common low end there is.
  assert.equal(outOfRangeNote({ rangeStatus: 'BelowRange', rangeLow: 0 }), 'below 0');
  assert.notEqual(outOfRangeNote({ rangeStatus: 'BelowRange', rangeLow: 0 }), 'below its declared range');
});
