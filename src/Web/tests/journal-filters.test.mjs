// Plain JavaScript and Node's own test runner, like the other tests here.
//
// The journal's filters, as the request they turn into (Phase 5.5's deferred work, done 2026-10-05).
//
// The thing worth testing is not that a query string is built -- it is WHICH query string, because
// two of these cases are the difference between a working filter and a silently broken one:
//
//   * an absent filter must be left out entirely, not sent empty. `tag=` with nothing after it is a
//     filter matching nothing, so a reader who had picked no tag would see an empty journal and
//     conclude the plant had been quiet.
//   * a filter must be REPEATED rather than joined, because the server reads it as a list and a
//     comma-joined value is one unknown tag id.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { journalQuery } from '../.node-test/models.js';

test('no filters sends only the limit, and nothing that looks like a filter', () => {
  const query = journalQuery({});

  assert.equal(query, 'limit=200');
  assert.ok(!query.includes('tag='), 'an absent tag filter must not be sent at all');
  assert.ok(!query.includes('type='), 'an absent type filter must not be sent at all');
});

test('one tag is sent once, under the name the server reads', () => {
  assert.equal(journalQuery({ tagIds: ['tag-1'] }), 'limit=200&tag=tag-1');
});

test('several tags are repeated rather than joined', () => {
  // A comma-joined value is one unknown tag id to a server reading a list, which would answer
  // "nothing" rather than the two tags asked for.
  const query = journalQuery({ tagIds: ['tag-1', 'tag-2'] });

  assert.equal(query, 'limit=200&tag=tag-1&tag=tag-2');
});

test('an empty list is the same as no filter', () => {
  // The case that matters, because it is what the client holds before a reader picks anything.
  const query = journalQuery({ tagIds: [], types: [] });

  assert.equal(query, 'limit=200');
});

test('types are repeated the same way, and both filters travel together', () => {
  const query = journalQuery({ tagIds: ['tag-1'], types: ['Raised', 'Cleared'] });

  assert.equal(query, 'limit=200&tag=tag-1&type=Raised&type=Cleared');
});

test('a limit is carried through, so a reader can ask for more than the default', () => {
  assert.equal(journalQuery({ limit: 500 }), 'limit=500');
});

test('a time window is sent as the bounds the server reads, and omitted when absent', () => {
  assert.equal(
    journalQuery({ from: '2026-10-05T00:00:00Z', to: '2026-10-06T00:00:00Z' }),
    'limit=200&from=2026-10-05T00%3A00%3A00Z&to=2026-10-06T00%3A00%3A00Z',
  );

  assert.ok(!journalQuery({ from: null, to: null }).includes('from='));
});
