// Plain JavaScript and Node's own test runner, like the other tests here.
//
// ADR-0024's authoring operations. Every one of them rewrites rows and positions to be dense and
// ascending, which is the property most worth testing: a list edited repeatedly by hand is exactly
// where a gap or a repeated position comes from, and a gap is an empty row on screen while a
// repeated position is two components swapping places on every render.
//
// The other property is told apart on purpose. These return NEW sets and never edit in place, so a
// caller holding the old one -- which the component does, to compare -- is not surprised by it
// changing underneath.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  addComponent,
  changeComponent,
  isNew,
  moveComponent,
  newComponent,
  removeComponent,
  reorderComponent,
  toSaveComponent,
  toSaveScreen,
} from '../.node-test/screen.js';

/** A saved component, with the fields a test does not care about filled in. */
function saved(overrides) {
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

test('adding to an empty row takes the whole width', () => {
  const added = addComponent([], newComponent('label', null, 'Heading'), 0);

  const only = added[0];
  assert.equal(only.rowIndex, 0);
  assert.equal(only.position, 0);
  assert.equal(only.columnSpan, 12);
});

test('adding beside something takes half the width, because a full one would be refused', () => {
  // Two full-width components in one row are twenty-four columns of a twelve-column grid, and the
  // server refuses that -- so a client defaulting to full width would build a screen it could not
  // save.
  const existing = [saved({})];

  const added = addComponent(existing, newComponent('status', 'tag-2', null), 0);

  assert.equal(added[1].columnSpan, 6);
  assert.equal(added[1].position, 1);
});

test('adding to a row that is not the first leaves the others alone', () => {
  const existing = [saved({ id: 'a', rowIndex: 0 }), saved({ id: 'b', rowIndex: 1 })];

  const added = addComponent(existing, newComponent('label', null, 'Later'), 1);

  assert.equal(added.find((component) => component.id === 'a').rowIndex, 0);
  assert.equal(added.find((component) => component.id === 'b').rowIndex, 1);
  assert.equal(added[2].rowIndex, 1);
  assert.equal(added[2].position, 1);
});

test('removing takes the component out and closes the gap its row left', () => {
  // An empty row on screen is a gap an author cannot see the cause of.
  const existing = [
    saved({ id: 'a', rowIndex: 0 }),
    saved({ id: 'b', rowIndex: 1 }),
    saved({ id: 'c', rowIndex: 2 }),
  ];

  const left = removeComponent(existing, 'b');

  assert.deepEqual(
    left.map((component) => component.id),
    ['a', 'c'],
  );
  assert.deepEqual(
    left.map((component) => component.rowIndex),
    [0, 1],
  );
});

test('removing something that is not there changes nothing rather than failing', () => {
  const existing = [saved({ id: 'a' })];

  assert.deepEqual(removeComponent(existing, 'nope'), existing);
});

test('changing a span leaves everything else about the component alone', () => {
  const existing = [saved({ id: 'a', columnSpan: 12, title: 'Kept', tagId: 'tag-9' })];

  const changed = changeComponent(existing, 'a', { columnSpan: 6 });

  assert.equal(changed[0].columnSpan, 6);
  assert.equal(changed[0].title, 'Kept');
  assert.equal(changed[0].tagId, 'tag-9');
});

test('changing a title to nothing is not the same as not mentioning it', () => {
  // The distinction the signature exists for: a label with no text is refused by the server, and a
  // clear button has to be able to say so.
  const existing = [saved({ id: 'a', title: 'Some text' })];

  assert.equal(changeComponent(existing, 'a', { title: null })[0].title, null);
  assert.equal(changeComponent(existing, 'a', {})[0].title, 'Some text');
});

test('moving a component to another row puts it last in that row', () => {
  // Four components on three rows, so that moving one cannot empty a row and the row numbers stay
  // meaningful. The empty-row case is the next test, which is a different thing.
  const existing = [
    saved({ id: 'a', rowIndex: 0 }),
    saved({ id: 'b', rowIndex: 1 }),
    saved({ id: 'c', rowIndex: 1, position: 1 }),
    saved({ id: 'd', rowIndex: 2 }),
  ];

  const moved = moveComponent(existing, 'a', 1);

  // Row 0 is gone, so row 1 is now row 0 and row 2 is now row 1 -- which is what renumbering means.
  const a = moved.find((component) => component.id === 'a');
  assert.equal(a.rowIndex, 0);
  assert.equal(a.position, 2, 'the end of the row it moved to');

  assert.deepEqual(
    moved.filter((component) => component.rowIndex === 0).map((component) => component.id),
    ['b', 'c', 'a'],
  );

  // Every row is dense from zero and every row's positions are dense from zero.
  assert.deepEqual([...new Set(moved.map((component) => component.rowIndex))], [0, 1]);
  for (const row of [0, 1]) {
    assert.deepEqual(
      moved
        .filter((component) => component.rowIndex === row)
        .map((component) => component.position)
        .sort((left, right) => left - right),
      moved.filter((component) => component.rowIndex === row).map((_, index) => index),
    );
  }
});

test('moving the only component of a row collapses that row rather than leaving a gap', () => {
  // An empty row on screen is a gap an author cannot see the cause of.
  const existing = [saved({ id: 'a', rowIndex: 0 }), saved({ id: 'b', rowIndex: 1 })];

  const moved = moveComponent(existing, 'a', 1);

  assert.deepEqual(
    moved.map((component) => component.rowIndex),
    [0, 0],
  );
  assert.deepEqual(
    moved.map((component) => component.position),
    [0, 1],
  );
});

test('moving to a place inside a row splices rather than appending', () => {
  const existing = [
    saved({ id: 'a', rowIndex: 0 }),
    saved({ id: 'b', rowIndex: 1 }),
    saved({ id: 'c', rowIndex: 1, position: 1 }),
  ];

  const moved = moveComponent(existing, 'a', 1, 1);

  assert.deepEqual(
    moved.filter((component) => component.rowIndex === 0).map((component) => component.id),
    ['b', 'a', 'c'],
  );
});

test('reordering swaps two neighbours within one row', () => {
  const existing = [
    saved({ id: 'a', rowIndex: 0, position: 0 }),
    saved({ id: 'b', rowIndex: 0, position: 1 }),
    saved({ id: 'c', rowIndex: 0, position: 2 }),
  ];

  const swapped = reorderComponent(existing, 'b', -1);

  const at = (id) => swapped.find((component) => component.id === id).position;
  assert.equal(at('b'), 0);
  assert.equal(at('a'), 1);
  assert.equal(at('c'), 2);
});

test('reordering past either end does nothing rather than wrapping', () => {
  // An author pressing "left" on the first component means nothing by it, and one that jumped to the
  // end of the row would be a surprise they then have to undo.
  const existing = [
    saved({ id: 'a', rowIndex: 0, position: 0 }),
    saved({ id: 'b', rowIndex: 0, position: 1 }),
  ];

  assert.deepEqual(reorderComponent(existing, 'a', -1), existing);
  assert.deepEqual(reorderComponent(existing, 'b', 1), existing);
});

test('reordering does not touch another row', () => {
  const existing = [
    saved({ id: 'a', rowIndex: 0, position: 0 }),
    saved({ id: 'b', rowIndex: 0, position: 1 }),
    saved({ id: 'c', rowIndex: 1, position: 0 }),
  ];

  const swapped = reorderComponent(existing, 'a', 1);

  assert.equal(swapped.find((component) => component.id === 'c').position, 0);
});

test('a component being added is marked as new and loses its placeholder on the way out', () => {
  // The placeholder id is a client invention. Sending it would be asking the server to store a
  // string that is not an id, and the server would refuse the whole screen.
  const added = newComponent('value', 'tag-1', null);

  assert.equal(isNew(added), true);

  const outgoing = toSaveComponent(added);
  assert.equal(outgoing.id, null);
  assert.equal(outgoing.tagId, 'tag-1');
});

test('a saved component keeps its id on the way out', () => {
  const outgoing = toSaveComponent(saved({ id: 'real-id' }));

  assert.equal(outgoing.id, 'real-id');
});

test('readable is not sent, because it is the server answer about the reader', () => {
  // An author does not get to state it, and a client that sent it would be one that believes it can
  // grant itself a binding.
  const outgoing = toSaveComponent(saved({ readable: false }));

  assert.equal(outgoing.readable, undefined);
  // The whole shape, so a field added to the outgoing component has to be added here too — which is
  // what caught `symbol` and `states` the day they were introduced (ADR-0027).
  assert.deepEqual(Object.keys(outgoing).sort(), [
    'columnSpan',
    'id',
    'kind',
    'position',
    'rowIndex',
    'states',
    'symbol',
    'tagId',
    'title',
  ]);
});

test('a whole screen goes out as its name, position and every component', () => {
  const screen = {
    id: 's1',
    siteId: 'site-1',
    name: 'Overview',
    position: 2,
    components: [saved({ id: 'a' }), newComponent('label', null, 'Text')],
  };

  const outgoing = toSaveScreen(screen);

  assert.equal(outgoing.name, 'Overview');
  assert.equal(outgoing.position, 2);
  assert.equal(outgoing.components.length, 2);
  assert.equal(outgoing.components[0].id, 'a');
  assert.equal(outgoing.components[1].id, null);
});

test('the operations never edit the set they were given', () => {
  // The component holds the old set to compare against and to fall back to when a save fails, so an
  // operation that edited in place would change the screen under the author's feet.
  const existing = [saved({ id: 'a', columnSpan: 12 })];
  const before = JSON.stringify(existing);

  addComponent(existing, newComponent('label', null, 'X'), 0);
  removeComponent(existing, 'a');
  changeComponent(existing, 'a', { columnSpan: 4 });
  moveComponent(existing, 'a', 3);
  reorderComponent(existing, 'a', 1);

  assert.equal(JSON.stringify(existing), before);
});
