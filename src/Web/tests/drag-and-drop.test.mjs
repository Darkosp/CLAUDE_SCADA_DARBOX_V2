// Where a drop lands (Phase 8's remaining authoring item).
//
// **The arithmetic is the whole of this, and it is off by one in one direction only** — which is why
// it is a pure function with tests rather than three lines in a drop handler.
//
// `moveComponent` builds its target row with the moved component already taken out. That is what lets
// one operation serve both "reorder within a row" and "move to another row", and it means its `at`
// counts places in a row that no longer contains the thing being moved. A drop handler reading the
// row the author is looking at — which *does* contain it — is one too high for every drop to the right
// of where the component started, and exactly right for every drop to the left. A defect like that
// looks like "sometimes it goes where I meant", which is the hardest kind to report.
//
// The tests below pin both directions, both rows, and the drops that mean nothing.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { dropPosition, moveComponent, newComponent } from '../.node-test/screen.js';

/** Four components in one row, named a b c d, plus two in a second row named e f. */
function screen() {
  const at = (name, rowIndex, position) => ({
    ...newComponent('label', null, name),
    id: name,
    rowIndex,
    position,
    columnSpan: 3,
  });

  return [
    at('a', 0, 0),
    at('b', 0, 1),
    at('c', 0, 2),
    at('d', 0, 3),
    at('e', 1, 0),
    at('f', 1, 1),
  ];
}

/** The order of a row after a move, as names — which is what an author is looking at. */
function rowOf(components, rowIndex) {
  return components
    .filter((component) => component.rowIndex === rowIndex)
    .sort((left, right) => left.position - right.position)
    .map((component) => component.id);
}

/** Drag `dragged` onto `onto`, and report both rows afterwards. */
function drop(dragged, onto, side) {
  const components = screen();
  const where = dropPosition(components, dragged, onto, side);

  assert.ok(where, `dropping ${dragged} ${side} ${onto} should land somewhere`);

  const after = moveComponent(components, dragged, where.rowIndex, where.at);
  return { row0: rowOf(after, 0), row1: rowOf(after, 1) };
}

test('dragging left within a row puts it where the author let go', () => {
  // The direction that was never going to be wrong, and it is here as the control: without it, a
  // function that subtracted one everywhere would pass the rightward tests below.
  assert.deepEqual(drop('d', 'b', 'before').row0, ['a', 'd', 'b', 'c']);
  assert.deepEqual(drop('d', 'b', 'after').row0, ['a', 'b', 'd', 'c']);
});

test('dragging RIGHT within a row puts it where the author let go, which is the off-by-one', () => {
  // Read these against the row the author sees: a b c d.
  //
  // Dropping `a` AFTER `c` must leave `c` with `a` on its right — b c a d. A handler that passed the
  // rendered index of `c`, which is 2, would ask for place 3 in a row that is now only b c d, and the
  // component would land after `d` instead. One place too far, every time, and only rightwards.
  assert.deepEqual(drop('a', 'c', 'after').row0, ['b', 'c', 'a', 'd']);
  assert.deepEqual(drop('a', 'c', 'before').row0, ['b', 'a', 'c', 'd']);
  assert.deepEqual(drop('a', 'd', 'after').row0, ['b', 'c', 'd', 'a']);
});

test('dragging to the other row takes it out of the first one', () => {
  const after = drop('a', 'f', 'before');

  assert.deepEqual(after.row0, ['b', 'c', 'd']);
  assert.deepEqual(after.row1, ['e', 'a', 'f']);
});

test('dropping onto the far end of another row appends to it', () => {
  const after = drop('a', 'f', 'after');

  assert.deepEqual(after.row0, ['b', 'c', 'd']);
  assert.deepEqual(after.row1, ['e', 'f', 'a']);
});

test('a drop that cannot mean anything lands nowhere, rather than somewhere', () => {
  const components = screen();

  // Onto itself: an author who picked something up and put it back has not asked for a change, and a
  // handler that moved it anyway would renumber the set for nothing.
  assert.equal(dropPosition(components, 'a', 'a', 'before'), null);
  assert.equal(dropPosition(components, 'a', 'a', 'after'), null);

  // Onto something that is not there. The caller does nothing with a null rather than guessing,
  // because a guess moves a component the author did not ask to move.
  assert.equal(dropPosition(components, 'a', 'ghost', 'before'), null);
  assert.equal(dropPosition(components, 'ghost', 'a', 'before'), null);
});

test('every drop keeps the set the same size, which a renumbering bug would not', () => {
  // The invariant worth asserting separately: dragging is rearranging, never adding or losing. A
  // `moveComponent` that appended instead of splicing would still satisfy some of the orders above.
  for (const [dragged, onto] of [['a', 'c'], ['d', 'b'], ['a', 'f'], ['f', 'a'], ['e', 'd']]) {
    for (const side of ['before', 'after']) {
      const components = screen();
      const where = dropPosition(components, dragged, onto, side);
      const after = moveComponent(components, dragged, where.rowIndex, where.at);

      assert.equal(after.length, components.length, `${dragged} ${side} ${onto} changed the count`);
      assert.deepEqual(
        [...after.map((component) => component.id)].sort(),
        [...components.map((component) => component.id)].sort(),
        `${dragged} ${side} ${onto} changed which components exist`,
      );
    }
  }
});

test('positions come back dense and ascending, so no row is drawn with a gap', () => {
  const components = screen();
  const where = dropPosition(components, 'a', 'f', 'after');
  const after = moveComponent(components, 'a', where.rowIndex, where.at);

  for (const rowIndex of [0, 1]) {
    const positions = after
      .filter((component) => component.rowIndex === rowIndex)
      .map((component) => component.position)
      .sort((left, right) => left - right);

    assert.deepEqual(positions, positions.map((_, index) => index), `row ${rowIndex} has a gap`);
  }
});
