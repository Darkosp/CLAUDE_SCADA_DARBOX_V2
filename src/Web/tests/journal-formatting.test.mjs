// Plain JavaScript against the compiled models.js, for the same reasons as
// parse-number-field.test.mjs: Node's own runner, no dependency, no @types/node.
//
// These cover what the Phase 5.5 manual gate found on the Journal screen — a raw double
// shown to an operator, and a gap window that reads as an interval running backwards
// when it crosses midnight.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { describeReason, formatGapWindow, formatMeasurement } from '../.node-test/models.js';

test('a double is shown as a measurement, not as its arithmetic', () => {
  // Exactly what the gate saw in the Note column.
  assert.equal(formatMeasurement(4.8100000000000005, 'bar'), '4.81 bar');
});

test('two decimals, matching the live value elsewhere', () => {
  assert.equal(formatMeasurement(4.2, 'bar'), '4.20 bar');
  assert.equal(formatMeasurement(4.2, null), '4.20');
});

test('nothing to show is a dash, not a zero', () => {
  assert.equal(formatMeasurement(null, 'bar'), '—');
  assert.equal(formatMeasurement(undefined, 'bar'), '—');
  assert.equal(formatMeasurement(Number.NaN, 'bar'), '—');
});

test('zero is a measurement like any other', () => {
  assert.equal(formatMeasurement(0, 'bar'), '0.00 bar');
});

test('a window within one day needs only times', () => {
  const from = new Date(2026, 8, 21, 15, 31, 33);
  const to = new Date(2026, 8, 21, 16, 13, 44);

  assert.equal(formatGapWindow(from, to), '15:31:33 – 16:13:44');
});

test('a window across midnight carries both dates', () => {
  // Without the dates this reads "23:55:00 – 14:13:44": an interval that ran backwards,
  // when it is an outage of fourteen hours.
  const from = new Date(2026, 8, 21, 23, 55, 0);
  const to = new Date(2026, 8, 22, 14, 13, 44);

  assert.equal(formatGapWindow(from, to), '2026-09-21 23:55:00 – 2026-09-22 14:13:44');
});

test('the same clock time on two different days is still two days', () => {
  const from = new Date(2026, 8, 21, 9, 5, 3);
  const to = new Date(2026, 8, 22, 9, 5, 3);

  assert.equal(formatGapWindow(from, to), '2026-09-21 09:05:03 – 2026-09-22 09:05:03');
});

test('single digits are padded', () => {
  const from = new Date(2026, 0, 2, 3, 4, 5);
  const to = new Date(2026, 0, 2, 6, 7, 8);

  assert.equal(formatGapWindow(from, to), '03:04:05 – 06:07:08');
});

test('a window with an end missing is no window', () => {
  assert.equal(formatGapWindow(new Date(2026, 8, 21), null), null);
  assert.equal(formatGapWindow(null, new Date(2026, 8, 21)), null);
  assert.equal(formatGapWindow(null, null), null);
});

test('a reason is shown in words', () => {
  assert.equal(describeReason('superseded-by-new-breach'), 'replaced by a new alarm');
  assert.equal(describeReason('superseded'), 'replaced by a later occurrence');
  assert.equal(describeReason('definition-removed'), 'threshold deleted');
  assert.equal(describeReason('retirement-completed-at-startup'), 'closed at startup');
});

test('a reason this client has not been taught is shown as it stands', () => {
  // Better a token the reader can ask about than a row that says nothing.
  assert.equal(describeReason('something-new-from-the-engine'), 'something-new-from-the-engine');
});

test('no reason is no note', () => {
  assert.equal(describeReason(null), null);
  assert.equal(describeReason(undefined), null);
  assert.equal(describeReason(''), null);
});
