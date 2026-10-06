// A trend that has stopped growing says so (ADR-0003).
//
// The defect these pin, found on 2026-10-06 by making a whole Site go Bad and looking at a screen:
// **a trend draws only Good numeric samples, which is right, so an outage does not stop it — it
// freezes it.** The line keeps its shape, the sample count keeps its number, and the tile goes on
// looking like a running plant while every value tile beside it says `BAD` and shows a dash.
//
// It is the same failure Phase 1's walk found in this same chart, in its second form: then the lie
// was a straight line drawn through a Gateway-downtime gap; now it is an old line left on screen.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { trendFreshness } from '../.node-test/screen.js';

/** A history sample as the API sends it. Only the timestamp matters to freshness. */
function sample(at, value = 3.4) {
  return {
    tagId: 'tag-1',
    sourceTimestampUtc: at,
    value: { kind: 'numeric', numeric: value },
    quality: 'Good',
  };
}

const NOW = new Date('2026-10-06T12:00:00Z');
const ago = (minutes) => new Date(NOW.getTime() - minutes * 60_000).toISOString();

test('a trend whose newest sample is recent is not stale', () => {
  const fresh = trendFreshness([sample(ago(30)), sample(ago(1))], NOW);

  assert.equal(fresh.stale, false);
  assert.equal(fresh.note, null, 'a fresh trend says nothing, so a note means something');
});

test('a trend whose newest sample is old is stale, and says since when', () => {
  // Two minutes of history that ENDED twenty minutes ago. The shape on screen is a healthy-looking
  // line, which is exactly the problem.
  const stale = trendFreshness([sample(ago(22)), sample(ago(20))], NOW);

  assert.equal(stale.stale, true);
  assert.match(stale.note, /no reading since \d{2}:\d{2}/);
});

test('a trend with no samples is empty rather than stale, because the chart already says so', () => {
  // Different facts, and conflating them would put "no reading since" on a tile that has never read.
  const empty = trendFreshness([], NOW);

  assert.equal(empty.stale, false);
  assert.equal(empty.note, null);
});

test('the newest sample is found by timestamp, not by position in the array', () => {
  // **Asserted because the API makes no promise about order.** Reading the last element would make
  // this depend on that order, and a history returned newest-first would report a stale trend as
  // fresh — the failure mode that matters, since it fails toward looking alive.
  const newestFirst = trendFreshness([sample(ago(1)), sample(ago(5)), sample(ago(9))], NOW);

  assert.equal(newestFirst.stale, false, 'the newest is at the front and the trend is fresh');

  const oldestFirst = trendFreshness([sample(ago(9)), sample(ago(5)), sample(ago(1))], NOW);

  assert.equal(oldestFirst.stale, false, 'and at the back, and it is still fresh');
});

test('a trend can be stale while its tag reads Good, which is the case nothing else catches', () => {
  // **The reason this test is about timestamps rather than quality.** A driver that stops delivering
  // without declaring itself Bad leaves a tag whose last reading is Good and old. Every other signal
  // on the screen agrees with it; only the age of the history disagrees. If this ever starts reading
  // the tag's quality instead, this test is what fails.
  const samples = [sample(ago(45)), sample(ago(40))];

  assert.equal(samples.every((s) => s.quality === 'Good'), true, 'every sample is Good');
  assert.equal(trendFreshness(samples, NOW).stale, true, 'and the trend is still stale');
});

test('the threshold is generous, because a scan every second is not a stale trend', () => {
  // The same five minutes `stalenessNote` uses, for the same reason: a scan and a wall clock do not
  // need reconciling, and half an hour does. A threshold short enough to fire on a slow scan would
  // put a warning on every healthy screen and teach readers to ignore it.
  assert.equal(trendFreshness([sample(ago(4))], NOW).stale, false);
  assert.equal(trendFreshness([sample(ago(6))], NOW).stale, true);
});

test('an unreadable timestamp is not treated as stale', () => {
  // Guessing here would mean a malformed history puts a warning on a screen that may be fine.
  const broken = trendFreshness([{ ...sample(ago(1)), sourceTimestampUtc: 'not a time' }], NOW);

  assert.equal(broken.stale, false);
});
