// A trend that has stopped growing says so (ADR-0003).
//
// The defect these pin, found on 2026-10-06 by making a whole Site go Bad and looking at a screen:
// **a trend draws only what can be plotted, which is right, so an outage does not stop it — it
// freezes it.** The line keeps its shape, the reading count keeps its number, and the tile goes on
// looking like a running plant while every value tile beside it says `BAD` and shows a dash.
//
// It is the same failure Phase 1's walk found in this same chart, in its second form: then the lie
// was a straight line drawn through a Gateway-downtime gap; now it is an old line left on screen.
//
// **The input changed on 2026-10-07 and the question did not** (ADR-0029): the chart holds buckets
// rather than readings now, so what is read here is `lastUtc` — the newest reading in each bucket.
// That distinction is load-bearing, and one test below exists because the ADR first stated it
// backwards: a bucket's `startUtc` is the edge of the stretch, not a time anything was measured at.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { trendFreshness } from '../.node-test/screen.js';

/** One bucket as the API sends it, with the two times freshness is about. */
function bucket(startedMinutesAgo, lastedMinutesAgo, count = 60, low = 3.4, high = 3.5) {
  return {
    startUtc: ago(startedMinutesAgo),
    lastUtc: ago(lastedMinutesAgo),
    count,
    low,
    high,
  };
}

const NOW = new Date('2026-10-06T12:00:00Z');
const ago = (minutes) => new Date(NOW.getTime() - minutes * 60_000).toISOString();

/** A wall clock as the note prints it, in this machine's own zone. */
const clockOf = (minutesAgo) => {
  const at = new Date(ago(minutesAgo));
  const pad = (value) => String(value).padStart(2, '0');
  return `${pad(at.getHours())}:${pad(at.getMinutes())}`;
};

test('a trend whose newest reading is recent is not stale', () => {
  const fresh = trendFreshness([bucket(30, 29), bucket(1, 0.5)], NOW);

  assert.equal(fresh.stale, false);
  assert.equal(fresh.note, null, 'a fresh trend says nothing, so a note means something');
});

test('a trend whose newest reading is old is stale, and says since when', () => {
  // Two minutes of history that ENDED twenty minutes ago. The shape on screen is a healthy-looking
  // line, which is exactly the problem.
  const stale = trendFreshness([bucket(22, 21), bucket(21, 20)], NOW);

  assert.equal(stale.stale, true);
  assert.match(stale.note, /no reading since \d{2}:\d{2}/);
});

test('a trend with no buckets is empty rather than stale, because the chart already says so', () => {
  // Different facts, and conflating them would put "no reading since" on a tile that has never read.
  const empty = trendFreshness([], NOW);

  assert.equal(empty.stale, false);
  assert.equal(empty.note, null);
});

test('the newest reading is found by timestamp, not by position in the array', () => {
  // **Asserted because the API makes no promise about order.** Reading the last element would make
  // this depend on that order — a newest-first array would report a fresh trend as stale and an
  // oldest-first one would do the reverse.
  const newestFirst = trendFreshness([bucket(1, 0.5), bucket(5, 4), bucket(9, 8)], NOW);

  assert.equal(newestFirst.stale, false, 'the newest is at the front and the trend is fresh');

  const oldestFirst = trendFreshness([bucket(9, 8), bucket(5, 4), bucket(1, 0.5)], NOW);

  assert.equal(oldestFirst.stale, false, 'and at the back, and it is still fresh');
});

test('the age is the newest reading, not the edge of the bucket it fell in', () => {
  // **This test is why ADR-0029 carries a correction.** The ADR first said a chart deciding age on
  // the bucket's start would report a dead instrument as fresh; written that way, this test refuses
  // to fail, because a start is always at or before the last reading in its bucket. The real defect
  // is the other direction, and it is the one a long window makes easy to hit: on a seven-day
  // window a bucket covers seventeen minutes, so a trend that stopped reading three minutes ago sits
  // in a bucket that began twenty minutes ago. Judged by the edge, a live instrument reads as dead.
  const live = trendFreshness([bucket(20, 3)], NOW);

  assert.equal(live.stale, false, 'the newest reading is three minutes old, whatever the bucket edge is');
  assert.equal(live.note, null);

  const stopped = trendFreshness([bucket(40, 20)], NOW);

  assert.equal(stopped.stale, true);
  // And the time it names is the reading's, not the bucket's: a note reading "no reading since
  // 11:20" beside a reading taken at 11:40 would be a time nothing was measured at (ADR-0003).
  assert.match(stopped.note, new RegExp(clockOf(20)));
  assert.doesNotMatch(stopped.note, new RegExp(clockOf(40)));
});

test('a bucket can hold readings and still be stale, which is the case nothing else catches', () => {
  // **The reason this test is about timestamps rather than about anything in the bucket.** A driver
  // that stops delivering without declaring itself Bad leaves readings that are present, plottable
  // and old. Every other signal on the screen agrees with them; only their age disagrees.
  const counted = bucket(45, 41, 12, 3.4, 3.6);

  assert.equal(counted.count > 0 && counted.low !== null, true, 'the bucket holds plottable readings');
  assert.equal(trendFreshness([counted], NOW).stale, true, 'and the trend is still stale');
});

test('the threshold is generous, because a scan every second is not a stale trend', () => {
  // The same five minutes `stalenessNote` uses, for the same reason: a scan and a wall clock do not
  // need reconciling, and half an hour does. A threshold short enough to fire on a slow scan would
  // put a warning on every healthy screen and teach readers to ignore it.
  assert.equal(trendFreshness([bucket(5, 4)], NOW).stale, false);
  assert.equal(trendFreshness([bucket(7, 6)], NOW).stale, true);
});

test('an unreadable timestamp is not treated as stale', () => {
  // Guessing here would mean a malformed history puts a warning on a screen that may be fine.
  const broken = [{ ...bucket(1, 0.5), lastUtc: 'not a time' }];

  assert.equal(trendFreshness(broken, NOW).stale, false);
});
