// Layout rules that a browser would otherwise be the only thing checking.
//
// These are the same kind of test as `theme-contrast.test.mjs`, and they exist for the same reason: a
// rule about how something LOOKS is a rule, and this project's own working rule is that a rule and
// every place it applies have to move together. What a screenshot cannot do is stop a later change
// from putting the defect back.
//
// The specific defect these pin: a value tile became writable, and the `Write…` control that ADR-0026
// puts on it ran the full width of the tile and read as an empty input. `.row > .cell` is a flex
// column, whose default `align-items: stretch` makes every child as wide as the tile — and the button
// had no width rule of its own. It had been that way since the tile was written; it had simply never
// rendered, because no walked stack had a writable tag.
//
// Read from the source rather than from a built bundle, because the defect was in the source and the
// bundle is a copy.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const screenView = readFileSync(new URL('../src/app/screen-view.ts', import.meta.url), 'utf8');

/** The component's stylesheet, which is a template literal inside the decorator. */
const styles = screenView.slice(screenView.indexOf('styles: `'));

test('a value tile is a flex column, which is why its children stretch by default', () => {
  // Asserted rather than assumed: this is the fact the next test is about. If the cell ever stops
  // being a flex column, the stretch problem goes away and the reasoning below should be re-read
  // rather than left standing on a stale premise.
  assert.match(
    styles,
    /\.row > \.cell \{[^}]*display:\s*flex[^}]*flex-direction:\s*column/,
    'the value tile is no longer a flex column, so the stretch below may no longer apply',
  );
});

test('the write control does not stretch to the width of its tile', () => {
  // The defect itself. Without an alignment of its own, a button in a flex column fills the tile.
  const rule = styles.match(/\.operate \{[^}]*\}/);

  assert.ok(rule, '.operate has no rule at all, so the control inherits the stretch');

  assert.match(
    rule[0],
    /align-self:\s*(start|flex-start|center)/,
    'the write control can stretch the full width of the tile again, which reads as an empty input',
  );
});

test('the write control is not softened into looking like part of the reading', () => {
  // The reversal to avoid. The temptation after seeing a loud button beside a number is to make it
  // quiet — but a write changes a plant, and ADR-0026's whole point is that the control which acts is
  // visible as a control. If this ever becomes a ghost button, that is a decision to make on purpose.
  const rule = styles.match(/\.operate \{[^}]*\}/)[0];

  assert.doesNotMatch(
    rule,
    /(background|border|color):\s*transparent/,
    'the write control was made to disappear into the reading, which hides the one control that acts',
  );
});
