// The trend's coordinate system is the element it is drawn in (ADR-0029, walk of 2026-10-07).
//
// **A fixed viewBox in a responsive box is a lie about time, and this test exists because nothing else
// could see it.** The chart said `viewBox="0 0 600 160"` while the card it filled was 1482 px wide, and
// SVG's default `preserveAspectRatio` scales such a drawing uniformly to fit the *shorter* side and
// centres it — so the curve was drawn in a **607 px strip in the middle of the card** while the axis
// beneath it ran edge to edge, and the two value ticks floated in the middle of the chart instead of at
// its left edge. Every reading sat at the wrong time by the width of the empty band: about seven hours
// on a day-long window.
//
// No test in this repository could see that. The bucket arithmetic was right, the counts were right,
// the caption was right, and only the *rendered* box disagreed with the coordinate system. It took a
// screenshot of the running client and then a probe of the live DOM, which reported: svg box 1482x162,
// viewBox 600x160, first drawn point at x=846 of 1887.
//
// **A symbol is allowed a fixed viewBox and a chart is not.** `symbol.ts` draws `0 0 120 96` and is
// meant to keep its shape and sit centred in whatever tile holds it — a pump is a drawing. The trend's
// x axis *is* the window, so its coordinate system has to be the box it is drawn in, or the picture
// says things happened at times they did not.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/app/trend-chart.ts', import.meta.url), 'utf8');

test('the chart builds its viewBox from the size it measured, not from constants', () => {
  const viewBox = source.match(/\[attr\.viewBox\]="[^"]*"/)?.[0];

  assert.ok(viewBox !== undefined, 'trend-chart.ts no longer sets a viewBox at all');
  assert.match(viewBox, /width\(\)/, `the viewBox does not use the measured width: ${viewBox}`);
  assert.match(viewBox, /height\(\)/, `the viewBox does not use the measured height: ${viewBox}`);

  // And no second, literal coordinate system in the same template: a `0 0 600 160` written into the
  // markup is exactly the defect, whether or not the binding above is also there.
  assert.doesNotMatch(
    source,
    /viewBox="0 0 \d+ \d+"/,
    'the template writes a fixed viewBox again — the drawing would letterbox inside its own card',
  );
});

test('the measurement is bound to the element, which is not there on the first render', () => {
  // **This is the half that cost the fix an attempt, and the reason it is asserted rather than
  // described.** Empty history draws the words "Not enough history yet" instead of a chart, so a
  // measurement taken once after the component's first render found no SVG, returned, and never looked
  // again: the viewBox stayed the constant and the defect stayed with it. Confirmed by re-probing the
  // running client, which still reported `0 0 600 160` with the first drawn point at x=846.
  //
  // So the size is bound to the element (`viewChild`) and re-taken whenever the card moves, and a
  // one-shot render hook is the defect rather than the fix.
  assert.match(source, /viewChild<ElementRef<SVGSVGElement>>\('chart'\)/, 'the chart element is not bound');
  assert.match(source, /#chart/, 'the chart element has no template reference to bind to');
  assert.doesNotMatch(source, /afterNextRender\(/, 'a one-shot hook cannot measure an element that arrives later');
  assert.match(
    source,
    /new ResizeObserver\(/,
    'nothing re-measures the chart when its card moves — the tree beside it, the window, a screen column',
  );
});

test('the size is the element it is drawn in, minus the border it draws', () => {
  // The SVG's viewport is its *content* box, and this chart draws a border. Measured off by a pixel on
  // either side, preserveAspectRatio letterboxes the drawing again — the same defect, two orders of
  // magnitude smaller — so the border is subtracted rather than ignored.
  assert.match(source, /getBoundingClientRect\(\)/, 'the size is not measured from the element');
  assert.match(source, /borderLeftWidth/, 'the border is not taken off the measured width');
  assert.match(source, /borderTopWidth/, 'the border is not taken off the measured height');
});

test('before the first measurement the chart still has a size to draw at', () => {
  // A zero-width first paint would divide by nothing and draw nothing; the fallback is the budget the
  // chart used to be drawn at, which is one point per pixel at that width.
  assert.match(source, /width = signal\(TREND_POINTS\)/, 'the width no longer falls back to TREND_POINTS');
  assert.match(source, /height = signal\(160\)/, 'the height no longer falls back to its own height');
});

test('the scan found the chart it is guarding', () => {
  // The control. A regex that matched nothing would make the tests above vacuous.
  assert.match(source, /selector: 'app-trend-chart'/, 'trend-chart.ts is not the component this guards');
  assert.match(source, /class="chart"/, 'the chart element itself is gone from the template');
});
