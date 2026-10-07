// What period a trend covers, and where a reading sits across it.
//
// **A curve with no stated period is a curve nobody can reason about.** Until 2026-10-07 the client
// asked for fifteen minutes, hard-coded in two places, and the chart said only how many samples it
// held and what their highest and lowest values were — nothing said whether the line covered a
// quarter of an hour or a week. An operator looking into something that happened two hours ago had
// no way to ask for it.
//
// The second test below is the one that matters most, and it is the same defect `f22b9e4` fixed for a
// gap in the MIDDLE of a trend, left standing at the ENDS: plotting against the first and last sample
// stretches a short burst of readings across the full width as though it had covered the whole
// window. A reader cannot tell "the device was offline until ten minutes ago" from "the device has
// been reporting all along", and both draw the same picture.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { TREND_WINDOWS, plotAcross, trendAxis, trendColumns, trendWindowOf } from '../.node-test/models.js';

const minutes = (n) => n * 60 * 1000;

test('the windows an operator can choose are offered shortest first', () => {
  // Order is the claim: a picker that jumped about would make the shortest hard to find, and it is
  // the one in use before this existed.
  const spans = TREND_WINDOWS.map((window) => window.milliseconds);

  assert.deepEqual(spans, [...spans].sort((left, right) => left - right));
  assert.equal(TREND_WINDOWS[0].milliseconds, minutes(15), 'what every trend showed before this');
  assert.ok(TREND_WINDOWS.some((window) => window.milliseconds >= 24 * 60 * minutes(1) / 60 * 60));
});

test('a window is found by its label, and an unknown label falls back rather than breaking', () => {
  assert.equal(trendWindowOf('1 hour').milliseconds, minutes(60));

  // A label from a newer build, or a stale one in a browser: a trend that drew nothing would be
  // worse than one that drew the shortest window and said so on its own axis.
  assert.equal(trendWindowOf('a fortnight'), TREND_WINDOWS[0]);
});

test('a reading is placed across the window ASKED FOR, not across the samples held', () => {
  // The defect. A device that came back ten minutes into a fifteen-minute window has five minutes of
  // readings; plotted against first-and-last they fill the chart, and the outage disappears.
  const from = 0;
  const to = minutes(15);
  const width = 600;

  // The first reading after the outage belongs two thirds of the way across, not at the left edge.
  assert.equal(plotAcross(minutes(10), from, to, width), 400);
  assert.equal(plotAcross(minutes(15), from, to, width), 600);

  // And the ends of the window itself.
  assert.equal(plotAcross(from, from, to, width), 0);
  assert.equal(plotAcross(minutes(7.5), from, to, width), 300);
});

test('a reading just outside the window sits at the edge rather than off the chart', () => {
  // The server's bounds are inclusive and two clocks are never identical, so a sample fractionally
  // outside is ordinary. Drawing it off the chart would break the line for a millisecond's
  // disagreement.
  const width = 600;

  assert.equal(plotAcross(-minutes(1), 0, minutes(15), width), 0);
  assert.equal(plotAcross(minutes(16), 0, minutes(15), width), 600);
});

test('a window of no width does not divide by zero', () => {
  // Reachable: a chart rendered in the same millisecond its window was computed.
  assert.equal(Number.isFinite(plotAcross(0, 5, 5, 600)), true);
});

test('an axis within one day shows times, and across midnight shows dates', () => {
  // The rule `formatGapWindow` arrived at by being wrong first: "23:50 – 00:10" reads as an interval
  // running backwards. A seven-day window crosses midnight every time, so this is not an edge case
  // here — it is the common case for every window above an hour.
  const sameDay = trendAxis(new Date(2026, 9, 7, 9, 5), new Date(2026, 9, 7, 9, 20));

  assert.equal(sameDay.start, '09:05');
  assert.equal(sameDay.end, '09:20');

  const overnight = trendAxis(new Date(2026, 9, 6, 23, 50), new Date(2026, 9, 7, 0, 10));

  assert.match(overnight.start, /06/);
  assert.match(overnight.end, /07/);
  assert.notEqual(overnight.start, '23:50');
});

test('both ends of an axis are formatted the same way, which is what makes them comparable', () => {
  // Half an axis dated and half not would read as a typo, and worse, as a shorter interval than it is.
  const overnight = trendAxis(new Date(2026, 9, 6, 23, 50), new Date(2026, 9, 7, 0, 10));

  assert.equal(
    overnight.start.includes('/'),
    overnight.end.includes('/'),
    'one end carries a date and the other does not',
  );
});

// ---- reducing a long window to something a person can read -------------------
//
// **Found by walking the picker**: eight hours of a tag scanned every second is 28,402 readings drawn
// into 600 pixels, and what appeared was a solid black block. The window picker was delivered and
// unusable above its shortest setting — worse than not having it, because a reader would believe they
// were looking at something.

test('a column keeps the extremes of everything that fell in it, not an average', () => {
  // The rule that matters. An average flattens the spike that made somebody open the trend, and
  // taking every n-th reading drops it outright: the one-second excursion that tripped an alarm is
  // exactly the sample a thinning pass throws away.
  const samples = [
    { time: 0, value: 4.0 },
    { time: 1, value: 9.9 },   // the spike
    { time: 2, value: 4.1 },
  ];

  const columns = trendColumns(samples, 0, 10, 1);

  assert.equal(columns.length, 1, 'all three fall in one column at this width');
  assert.equal(columns[0].high, 9.9, 'the spike must survive');
  assert.equal(columns[0].low, 4.0);
});

test('a short trend is not reduced at all', () => {
  // Below one reading per column there is nothing to do, and a fifteen-minute trend must look exactly
  // as it did before this existed.
  const samples = [
    { time: 0, value: 1 },
    { time: 500, value: 2 },
    { time: 1000, value: 3 },
  ];

  const columns = trendColumns(samples, 0, 1000, 600);

  assert.equal(columns.length, 3);
  assert.deepEqual(columns.map((column) => column.high), [1, 2, 3]);
});

test('columns come back in order across the chart', () => {
  // The line is drawn by walking them, so out-of-order columns would draw it folding back on itself.
  const samples = Array.from({ length: 500 }, (_, index) => ({ time: index * 10, value: index % 7 }));
  const columns = trendColumns(samples, 0, 5000, 100);

  const xs = columns.map((column) => column.x);
  assert.deepEqual(xs, [...xs].sort((left, right) => left - right));
  assert.ok(columns.length <= 101, `reduced to ${columns.length} columns, not 500`);
});

test('a long window really is reduced, which is the whole point', () => {
  // The measured case, in miniature: eight hours at one reading a second.
  const eightHours = 8 * 60 * 60 * 1000;
  const samples = Array.from({ length: 28_402 }, (_, index) => ({
    time: index * (eightHours / 28_402),
    value: 3.4 + (index % 100) / 100,
  }));

  const columns = trendColumns(samples, 0, eightHours, 600);

  assert.ok(columns.length <= 601, `${columns.length} columns for 600 pixels`);
  // And the envelope still spans what the readings did, so nothing was flattened away.
  assert.ok(Math.max(...columns.map((column) => column.high)) >= 4.39);
  assert.ok(Math.min(...columns.map((column) => column.low)) <= 3.41);
});

test('no samples reduce to no columns rather than to anything invented', () => {
  assert.deepEqual(trendColumns([], 0, 1000, 600), []);
});
