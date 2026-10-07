// A trend draws buckets, and where each one belongs (ADR-0029).
//
// **This is the arithmetic that stayed in the browser when the reduction left it.** The server now
// sends at most one bucket per point the chart can draw, so what is left here is the part only the
// chart knows: where across the window each bucket sits, and where the line must break.
//
// The reduction itself — min and max of what was measured in each bucket, never an average — moved
// to the query, and its tests moved with it. What this file pins is that the client does not undo it:
// no bucket is drawn that carries no value, and no bucket that is missing is drawn through.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  NO_TREND,
  TREND_POINTS,
  bucketColumns,
  bucketGaps,
  readingsIn,
  trendSeries,
} from '../.node-test/models.js';

const minutes = (n) => n * 60 * 1000;

/** A reduced answer as the API sends it: one bucket per minute over a quarter of an hour. */
function series(buckets, bucketMilliseconds = minutes(1)) {
  return { bucketMilliseconds, buckets };
}

/** One bucket, with the fields the chart reads. */
function bucket(ordinal, { count = 60, low = 3.4, high = 3.6, bucketMilliseconds = minutes(1) } = {}) {
  const width = bucketMilliseconds;
  const start = ordinal * width;

  return {
    startUtc: new Date(start).toISOString(),
    lastUtc: new Date(start + width - 1000).toISOString(),
    count,
    low,
    high,
  };
}

test('a bucket is drawn at the middle of the stretch it covers', () => {
  // Not at its start: the readings inside it are within one bucket width of that position by
  // definition, and the middle is the honest place to draw what is known about them once their own
  // times are no longer in the payload. It is also what keeps the last bucket from being drawn a
  // whole width past the right edge of the window.
  const from = 0;
  const to = minutes(15);
  const width = 600;
  const columns = bucketColumns(series([bucket(0)]), from, to, width);

  // The first bucket covers 0–1 minute, so its middle is halfway across the first of fifteen minutes.
  assert.equal(columns.length, 1);
  assert.equal(columns[0].x, (width / 15) * 0.5);
  assert.ok(columns[0].x > 0 && columns[0].x < width);
});

test('a bucket the server measured nothing plottable in is a hole, not a value', () => {
  // The server sends it — with a count, because readings did arrive and the device was answering —
  // and it carries no envelope. Drawing it as a value would be the fabrication ADR-0003 refuses, and
  // dropping it without breaking the line would draw a ramp across a stretch nothing was measured in.
  const buckets = [bucket(0), { ...bucket(1), low: null, high: null }, bucket(2)];
  const columns = bucketColumns(series(buckets), 0, minutes(15), 600);

  assert.equal(columns.length, 2, 'the empty-envelope bucket is not a column');
  assert.deepEqual(columns.map((column) => column.ordinal), [0, 2]);
  assert.deepEqual([...bucketGaps(columns)], [0], 'and the line breaks where it was');
});

test('a missing bucket breaks the line, and at a resolution an outage used to hide behind', () => {
  // **The rule that replaced a heuristic, and the reason it replaced it.** Four times the median
  // distance between columns cannot see a hole narrower than about four columns — and on a seven-day
  // window a bucket is 1008 s, so four of them is over an hour of nothing drawn as a slope. A
  // missing bucket is exact at every resolution: the server sends the stretches it measured in.
  const sevenDays = 7 * 24 * 60 * minutes(1);
  const width = 1008000;

  const present = [0, 1, 2, 4, 5, 6, 7];
  const columns = bucketColumns(
    series(present.map((ordinal) => bucket(ordinal, { bucketMilliseconds: width })), width),
    0,
    sevenDays,
    TREND_POINTS,
  );

  assert.deepEqual(columns.map((column) => column.ordinal), present);
  // One bucket is missing between ordinal 2 and 4: the line breaks after the column at index 2, and
  // nowhere else — a hole of half an hour, not the hour and a half the old rule needed to see it.
  assert.deepEqual([...bucketGaps(columns)], [2]);
});

test('columns come back in order even when the buckets do not', () => {
  // The API promises no order, and the line is drawn by walking these: out-of-order columns would
  // draw it folding back on itself. The same reasoning as the freshness rule next door.
  const shuffled = [bucket(2), bucket(0), bucket(1)];
  const columns = bucketColumns(series(shuffled), 0, minutes(15), 600);

  assert.deepEqual(columns.map((column) => column.ordinal), [0, 1, 2]);
  assert.deepEqual([...bucketGaps(columns)], [], 'and consecutive buckets are not a gap');
});

test('a bucket whose start cannot be read is skipped rather than placed at the origin', () => {
  // Guessing here would put a reading at an x nobody measured it at, which is worse than a gap.
  const broken = { ...bucket(1), startUtc: 'not a time' };
  const columns = bucketColumns(series([broken, bucket(2)]), 0, minutes(15), 600);

  assert.deepEqual(columns.map((column) => column.ordinal), [2]);
});

test('the readings behind the curve are the sum of the buckets, which is what the caption says', () => {
  // Every reading in the window falls in exactly one bucket and every non-empty bucket is sent, so
  // this is exact rather than an estimate — and it is why a reduced chart can still say how much is
  // behind it (ADR-0029 §4).
  const buckets = [bucket(0, { count: 60 }), bucket(1, { count: 58 }), bucket(2, { count: 60 })];

  assert.equal(readingsIn(series(buckets)), 178);
  assert.equal(readingsIn(NO_TREND), 0);
});

test('a reduced answer becomes a series, and a raw answer is refused rather than reduced here', () => {
  // **The refusal is the point**, and it is where the deleted code would come back if it came back
  // at all: this client asks for a reduction, so an answer holding every reading — 100,552 of them
  // and 22 MB for seven days of one tag — is one it cannot draw and must not try to.
  const reduced = trendSeries({
    tagId: 'tag-1',
    tagName: 'Pressure',
    deviceName: 'PLC',
    isDeleted: false,
    samples: [],
    bucketMilliseconds: minutes(1),
    buckets: [bucket(0)],
  });

  assert.equal(reduced.bucketMilliseconds, minutes(1));
  assert.equal(reduced.buckets.length, 1);

  const raw = trendSeries({
    tagId: 'tag-1',
    tagName: 'Pressure',
    deviceName: 'PLC',
    isDeleted: false,
    samples: [{ value: { kind: 'numeric', numeric: 3.4 }, sourceTimestampUtc: new Date(0).toISOString(), ingestedAtUtc: new Date(0).toISOString(), quality: 'Good' }],
    bucketMilliseconds: null,
    buckets: [],
  });

  assert.equal(raw, null, 'no width means no series, and no series means nothing is drawn');
});

test('a series with no buckets says nothing rather than drawing something', () => {
  // The state a screen is in while it has no history, and the state a failed read leaves it in.
  // Zero width is not a division waiting to happen: there is no bucket to place, so nothing divides.
  assert.deepEqual(bucketColumns(NO_TREND, 0, minutes(15), 600), []);
  assert.deepEqual([...bucketGaps([])], []);
  assert.equal(NO_TREND.bucketMilliseconds, 0);
});
