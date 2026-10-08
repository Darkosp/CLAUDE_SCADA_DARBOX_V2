// The audit trail's query string and its "how much of it am I looking at" sentence (ADR-0032).
//
// Two rules live here, and both fail silently when they break:
//
// - **an absent filter is left out**, because `action=` with nothing after it is a filter matching
//   nothing — an Admin who had typed nothing would be shown an empty trail and would read it as a
//   system that had done nothing, which is the one thing a trail must never look like;
// - **a capped page says it is capped**, because a limit nobody mentions looks like a quiet night.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { auditPageNote, auditQuery } from '../.node-test/models.js';

const entry = (id) => ({
  id,
  occurredAtUtc: '2026-10-07T21:45:00+00:00',
  actorUserId: null,
  actor: null,
  action: 'auth.login',
  entityType: null,
  entityId: null,
  detail: '{}',
});

test('an absent filter is left out of the query rather than sent empty', () => {
  const query = auditQuery();

  assert.equal(query, 'limit=200');
  assert.ok(!query.includes('action='), 'an empty action would match nothing at all');
  assert.ok(!query.includes('before='), 'no cursor means the newest page, not an empty one');
});

test('a typed action is sent as the prefix it is', () => {
  assert.equal(auditQuery({ action: 'auth.' }), 'limit=200&action=auth.');

  // Trimmed, because a trailing space is a prefix that matches nothing and looks like a quiet system.
  assert.equal(auditQuery({ action: '  auth.  ' }), 'limit=200&action=auth.');

  // Whitespace alone is absence, not a filter.
  assert.equal(auditQuery({ action: '   ' }), 'limit=200');
});

test('the cursor is sent by presence, not by truth', () => {
  // Zero is falsy and is also not a row id, so a truth test would drop the cursor — the same class of
  // mistake as sending a filter empty, and just as invisible.
  assert.equal(auditQuery({ before: 0 }), 'limit=200&before=0');
  assert.equal(auditQuery({ before: 412 }), 'limit=200&before=412');
  assert.equal(auditQuery({ before: null }), 'limit=200');
});

test('the page note says how much of the trail is on screen', () => {
  assert.equal(auditPageNote({ entries: [entry(3)], total: 1 }), '1 entry, all of them.');

  const capped = auditPageNote({ entries: [entry(3), entry(2)], total: 4312 });
  assert.match(capped, /newest 2 of 4312/);

  // And the three states a reader can be in are told apart: nothing matches, everything is shown, or
  // there is more to see.
  assert.equal(auditPageNote({ entries: [], total: 0 }), 'Nothing recorded matches.');
  assert.match(auditPageNote({ entries: [], total: 9 }), /Nothing older/);
  assert.equal(auditPageNote({ entries: [entry(9)], total: 1 }), '1 entry, all of them.');
});
