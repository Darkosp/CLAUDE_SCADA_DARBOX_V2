// Plain JavaScript and Node's own test runner, like the other tests here.
//
// ADR-0024 in the client, and the decisions worth testing are the ones about honesty rather
// than about layout. The resolver is where a screen decides what to say when it cannot read
// something, so most of what follows is about telling four different situations apart:
//
//   * a reading that is present and its quality is Bad      -> a dash, WITH its quality shown
//   * a reading this session may not see                    -> still rendered, saying so
//   * a reading this session may see and does not have      -> "No reading", which is not a dash
//   * a component naming no tag at all                      -> said out loud
//
// Collapsing any two of those is the failure this file exists to catch: an operator looking at a
// screen has no other way to tell "no value" from "a value that is not good".

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { groupIntoRows, resolveComponent } from '../.node-test/screen.js';

/** A component as the API sends it, with the fields a test does not care about filled in. */
function component(overrides) {
  return {
    id: 'c1',
    rowIndex: 0,
    columnSpan: 12,
    position: 0,
    kind: 'value',
    title: null,
    tagId: 'tag-1',
    readable: true,
    ...overrides,
  };
}

/** A snapshot as the gateway pushes it. */
function snapshot(overrides) {
  return {
    tagId: 'tag-1',
    path: 'Skopje / Pump House / Discharge Pressure',
    value: { kind: 'numeric', numeric: 4.25 },
    sourceTimestampUtc: '2026-10-05T12:00:00Z',
    quality: 'Good',
    unitSymbol: 'bar',
    ...overrides,
  };
}

const site = 'site-1';
const none = new Map();

test('a value component carries its quality and its source time beside the number', () => {
  const resolved = resolveComponent(
    component({ kind: 'value' }),
    new Map([['tag-1', snapshot({})]]),
    [],
    site,
  );

  assert.equal(resolved.kind, 'value');
  assert.equal(resolved.text, '4.25');
  assert.equal(resolved.quality, 'Good');
  assert.equal(resolved.sourceTimestampUtc, '2026-10-05T12:00:00Z');
});

test('a Bad reading shows no number, and still shows that it is Bad', () => {
  // The whole of ADR-0003 as it reaches a screen. A Bad value that rendered as its last number, or
  // as 0, would be a fabricated reading; one that rendered as a dash with no quality would leave an
  // operator unable to tell it from a tag that has never been read.
  const resolved = resolveComponent(
    component({ kind: 'value' }),
    new Map([['tag-1', snapshot({ quality: 'Bad', value: { kind: 'none' } })]]),
    [],
    site,
  );

  assert.equal(resolved.kind, 'value');
  assert.equal(resolved.text, '—');
  assert.equal(resolved.quality, 'Bad');
});

test('a Stale reading shows its last value and says it is Stale', () => {
  const resolved = resolveComponent(
    component({ kind: 'value' }),
    new Map([['tag-1', snapshot({ quality: 'Stale' })]]),
    [],
    site,
  );

  assert.equal(resolved.text, '4.25');
  assert.equal(resolved.quality, 'Stale');
});

test('a binding this session may not see is still rendered, and says so', () => {
  // ADR-0024 5. The alternative -- hiding the component -- makes a screen look complete while
  // showing less than it was built to show, and the operator cannot know a tile is missing from a
  // screen they did not author.
  const resolved = resolveComponent(component({ readable: false }), none, [], site);

  assert.equal(resolved.kind, 'unreadable');
  assert.match(resolved.note, /not available/i);
});

test('a readable tag this client has no snapshot for reads as missing, not as a dash', () => {
  // Distinct from Bad on purpose: a dash is what a value that is not good looks like, and a tag
  // that has never reported looks like nothing at all. Saying "No reading" is the difference.
  const resolved = resolveComponent(component({}), none, [], site);

  assert.equal(resolved.kind, 'missing');
  assert.match(resolved.note, /no reading/i);
});

test('a component naming no tag is said out loud rather than drawn blank', () => {
  // The server refuses this at save time, so reaching it means the row came from somewhere other
  // than the API. Silence would make that indistinguishable from a working screen.
  const resolved = resolveComponent(component({ tagId: null }), none, [], site);

  assert.equal(resolved.kind, 'missing');
  assert.match(resolved.note, /names no tag/i);
});

test('a label shows what the author wrote and reads no tag', () => {
  const resolved = resolveComponent(
    component({ kind: 'label', tagId: null, title: 'Pump House' }),
    none,
    [],
    site,
  );

  assert.equal(resolved.kind, 'label');
  assert.equal(resolved.text, 'Pump House');
});

test('an alarms component shows this screen\'s Site and not the others', () => {
  // A session may see several Sites. An `alarms` component is about the Site its screen is on, so
  // another Site's alarms on it would be a summary of something the screen is not about.
  const alarms = [
    { occurrenceId: 'a1', siteId: 'site-1', tagPath: 'here' },
    { occurrenceId: 'a2', siteId: 'site-2', tagPath: 'elsewhere' },
  ];

  const resolved = resolveComponent(component({ kind: 'alarms', tagId: null }), none, alarms, site);

  assert.equal(resolved.kind, 'alarms');
  assert.deepEqual(
    resolved.alarms.map((alarm) => alarm.occurrenceId),
    ['a1'],
  );
});

test('a status component is the quality alone, with no value to misread', () => {
  const resolved = resolveComponent(
    component({ kind: 'status' }),
    new Map([['tag-1', snapshot({ quality: 'Uncertain' })]]),
    [],
    site,
  );

  assert.equal(resolved.kind, 'status');
  assert.equal(resolved.quality, 'Uncertain');
  assert.equal(resolved.text, undefined);
});

test('a trend component names the tag so the renderer can ask for its history', () => {
  const resolved = resolveComponent(
    component({ kind: 'trend' }),
    new Map([['tag-1', snapshot({})]]),
    [],
    site,
  );

  assert.equal(resolved.kind, 'trend');
  assert.equal(resolved.tagId, 'tag-1');
  assert.equal(resolved.path, 'Skopje / Pump House / Discharge Pressure');
});

test('rows are grouped by row and ordered by position within one', () => {
  const bottom = component({ id: 'bottom', rowIndex: 1, position: 0 });
  const second = component({ id: 'second', rowIndex: 0, position: 1 });
  const first = component({ id: 'first', rowIndex: 0, position: 0 });

  const rows = groupIntoRows([bottom, second, first]);

  assert.deepEqual(
    rows.map((row) => row.rowIndex),
    [0, 1],
  );
  assert.deepEqual(
    rows[0].components.map((entry) => entry.id),
    ['first', 'second'],
  );
  assert.deepEqual(
    rows[1].components.map((entry) => entry.id),
    ['bottom'],
  );
});

test('a screen with nothing on it produces no rows rather than one empty one', () => {
  assert.deepEqual(groupIntoRows([]), []);
});
