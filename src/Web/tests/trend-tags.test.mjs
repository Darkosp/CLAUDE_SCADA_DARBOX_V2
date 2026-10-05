// Plain JavaScript and Node's own test runner, like the other tests here.
//
// Which tags a screen's components need history for, and why this is a function rather than two
// filters that look alike.
//
// Phase 8's walk found the same shape of defect twice: **a rule and every place that applies it have
// to move together.** The preview and the read view both decide which trends need a series fetched,
// and if they answered differently the preview would show one thing and the operator another. They
// now call one function, and this pins what it decides — including the three exclusions, each of
// which is a request the API cannot answer or a component that should not say "Reading…".

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { trendTagIds } from '../.node-test/screen.js';

/** A component with the defaults that do not matter here filled in. */
function component(overrides) {
  return {
    id: 'component-1',
    rowIndex: 0,
    columnSpan: 1,
    position: 0,
    kind: 'trend',
    title: null,
    tagId: 'tag-1',
    readable: true,
    writable: false,
    ...overrides,
  };
}

test('a readable trend bound to a tag wants that tag', () => {
  assert.deepEqual(trendTagIds([component({})]), ['tag-1']);
});

test('two trends on one tag ask for one series', () => {
  // The history of a trend is the history of its tag, so a screen that draws the same tag twice
  // needs one fetch. Fetching twice would be wasteful and could draw two different windows of the
  // same series if they arrived either side of a sample.
  const tags = trendTagIds([
    component({ id: 'a', tagId: 'tag-1' }),
    component({ id: 'b', tagId: 'tag-1' }),
  ]);

  assert.deepEqual(tags, ['tag-1']);
});

test('two trends on different tags ask for both, in order', () => {
  const tags = trendTagIds([
    component({ id: 'a', tagId: 'tag-1' }),
    component({ id: 'b', tagId: 'tag-2' }),
  ]);

  assert.deepEqual(tags, ['tag-1', 'tag-2']);
});

test('a component that is not a trend wants nothing, however it is bound', () => {
  // A `value` component shows the live snapshot, not a series, so asking for its history would be
  // work nobody reads -- and on a screen of twelve values that is twelve requests.
  for (const kind of ['value', 'label', 'alarms', 'status']) {
    assert.deepEqual(trendTagIds([component({ kind })]), [], `${kind} should need no history`);
  }
});

test('a trend this session may not read wants nothing, so it does not say Reading forever', () => {
  // The exclusion that is about the sentence on screen rather than about the request. An unreadable
  // trend renders as unreadable (ADR-0024 section 5); if it were fetched, the entry would never
  // arrive and the component would sit on "Reading...", which claims the server is about to answer
  // when the truth is that this reader will never be shown it.
  assert.deepEqual(trendTagIds([component({ readable: false })]), []);
});

test('a trend bound to nothing wants nothing', () => {
  // A component an author has just added and not yet bound. `null` is not a tag id, and asking the
  // API for the history of null is not a question it has an answer to.
  assert.deepEqual(trendTagIds([component({ tagId: null })]), []);
});

test('the exclusions combine rather than cancelling each other', () => {
  const tags = trendTagIds([
    component({ id: 'wanted', tagId: 'tag-1' }),
    component({ id: 'unreadable', tagId: 'tag-2', readable: false }),
    component({ id: 'unbound', tagId: null }),
    component({ id: 'not-a-trend', tagId: 'tag-3', kind: 'value' }),
    component({ id: 'also-wanted', tagId: 'tag-4' }),
  ]);

  assert.deepEqual(tags, ['tag-1', 'tag-4']);
});

test('no components at all wants nothing, which is a screen with no trends', () => {
  assert.deepEqual(trendTagIds([]), []);
});
