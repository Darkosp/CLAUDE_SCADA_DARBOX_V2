// Plain JavaScript and Node's own test runner, like the other tests here.
//
// What a `value` component's resolved state has to carry for a write to be possible at all
// (ADR-0026).
//
// The dialog needs three things the read view did not: WHICH tag to write to, WHAT KIND of value it
// takes, and WHETHER this session may write it. The first two were not on the resolved component
// before this slice, and the failure mode if either goes missing is quiet — the tag id would be
// looked up again from somewhere else and could be a different tag than the reading beside it, and a
// missing kind would make the dialog treat every tag as a number.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { resolveComponent } from '../.node-test/screen.js';

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
    writable: true,
    ...overrides,
  };
}

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

test('a value component carries the tag a write would be addressed to', () => {
  // On the resolved component rather than looked up again, so that what is written to is the thing
  // whose reading is on screen beside the control.
  const resolved = resolveComponent(
    component({ tagId: 'tag-1' }),
    new Map([['tag-1', snapshot({})]]),
    [],
    site,
  );

  assert.equal(resolved.kind, 'value');
  assert.equal(resolved.tagId, 'tag-1');
});

test('a value component carries the kind of value the tag takes', () => {
  const numeric = resolveComponent(
    component({}),
    new Map([['tag-1', snapshot({})]]),
    [],
    site,
  );

  assert.equal(numeric.valueKind, 'numeric');

  const boolean = resolveComponent(
    component({}),
    new Map([['tag-1', snapshot({ value: { kind: 'boolean', boolean: true } })]]),
    [],
    site,
  );

  assert.equal(boolean.valueKind, 'boolean');
});

test('whether a write may be offered comes from the component and not from the kind', () => {
  // ADR-0026 decision 2: the server decides, and the client does no permission reasoning. A
  // component the server says is not writable resolves as not writable even though a write to a
  // numeric tag is perfectly possible.
  const refused = resolveComponent(
    component({ writable: false }),
    new Map([['tag-1', snapshot({})]]),
    [],
    site,
  );

  assert.equal(refused.kind, 'value');
  assert.equal(refused.writable, false);

  const allowed = resolveComponent(
    component({ writable: true }),
    new Map([['tag-1', snapshot({})]]),
    [],
    site,
  );

  assert.equal(allowed.writable, true);
});

test('an unreadable component resolves as unreadable and therefore carries no write target', () => {
  // The order of the resolver's checks is the honesty rule (ADR-0024 section 5): a reader who may
  // not see the value is not offered a control that writes it, and the resolved state has no tag for
  // a dialog to address -- there is no way to reach a write from an unreadable component.
  const resolved = resolveComponent(component({ readable: false }), new Map(), [], site);

  assert.equal(resolved.kind, 'unreadable');
  assert.equal(resolved.tagId, undefined);
  assert.equal(resolved.writable, undefined);
});

test('a tag with no reading yet resolves as missing, so no write can be offered from it', () => {
  // A tag that has never been measured has no reading for the dialog to show beside the field. The
  // operator would be writing blind, which decision 4 exists to prevent -- so there is no control.
  const resolved = resolveComponent(component({}), new Map(), [], site);

  assert.equal(resolved.kind, 'missing');
  assert.equal(resolved.tagId, undefined);
});

test('the other kinds carry no write target either', () => {
  // ADR-0026 decision 1: one component writes and nothing else does. A status or trend component
  // resolves without a tag for a dialog to address, which is what makes that structural rather than
  // a rule a template has to remember.
  for (const kind of ['status', 'trend']) {
    const resolved = resolveComponent(
      component({ kind }),
      new Map([['tag-1', snapshot({})]]),
      [],
      site,
    );

    assert.equal(resolved.kind, kind);
    assert.notEqual(resolved.valueKind, 'numeric');
  }
});
