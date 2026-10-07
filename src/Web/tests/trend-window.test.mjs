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

import { TREND_POINTS, TREND_WINDOWS, plotAcross, trendAxis, trendWindowOf } from '../.node-test/models.js';

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
//
// **The reduction itself is not in this file any more, because it is not in the client any more.**
// From 2026-10-07 until ADR-0029 the browser reduced the readings it had been sent, and the tests for
// that were here. They moved: the extremes rule to the query's own tests
// (`tests/Persistence.Tests/HistorizedHistoryTests.cs`, where the SQL is), and the arithmetic of
// placing buckets and finding holes to `trend-buckets.test.mjs`.

test('a trend asks for a bounded number of points, and says how many it was given', () => {
  // ADR-0029 §3. The budget is what bounds the answer: without it the server answers with every reading
  // in the window, and the reason this exists is that seven days of a tag scanned once a second came
  // back as 100,552 readings and 22.2 MB.
  //
  // **It is deliberately not the width the chart draws at**, which it measures for itself: on a wide
  // card the same points are spread across more pixels, which is a smooth line rather than a loss, and
  // the caption states the resolution ("593 points"). Asking for exactly the measured width is the next
  // step and needs the budget to be the widest trend of a tag on the screen — `open-work.md` §2.6.
  assert.equal(TREND_POINTS, 600);
  assert.ok(TREND_POINTS >= 2, 'and it is a number a trend can be drawn from at all');
});

