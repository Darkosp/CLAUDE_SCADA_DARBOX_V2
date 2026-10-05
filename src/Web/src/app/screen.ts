import { Alarm } from './models';
import { formatValue, TagSnapshot } from './tag';

/** The component kinds this build renders. The server refuses any other (ADR-0024 §3). */
export type ScreenComponentKind = 'label' | 'value' | 'trend' | 'alarms' | 'status';

/** One thing on a screen, as the API sends it. */
export interface ScreenComponent {
  id: string;
  rowIndex: number;
  columnSpan: number;
  position: number;
  kind: ScreenComponentKind;
  title: string | null;
  tagId: string | null;
  /**
   * Whether this session may see the value behind `tagId`.
   *
   * Decided by the server, not here, so that every renderer gives the same answer and there is one
   * place to check (ADR-0024 §5). Always true for a component that reads no tag.
   */
  readable: boolean;
  /**
   * Whether this session could write the tag behind `tagId` — **if writing from a screen existed,
   * which it does not** (ADR-0024 §9).
   *
   * ADR-0024 requires that a writable tag is *marked* writable on a screen and stops there; acting on
   * it is the next slice. Decided by the server for the same reason `readable` is, and false for a
   * reader who cannot operate the Site even when the tag itself is writable.
   */
  writable: boolean;
}

/** One operator screen, as the API sends it. */
export interface Screen {
  id: string;
  siteId: string;
  name: string;
  position: number;
  components: ScreenComponent[];
}

/**
 * What a component comes to on screen.
 *
 * A discriminated union rather than a bag of optional fields, so that a renderer has to say what it
 * does with each case and cannot accidentally print a value it does not have.
 */
export type ResolvedScreenComponent =
  | { kind: 'label'; id: string; text: string }
  | {
      kind: 'value';
      id: string;
      text: string;
      quality: string;
      sourceTimestampUtc: string | null;
      path: string;
      /** ADR-0024 §9: marked, never actionable in this phase. False for a kind that reads no tag. */
      writable: boolean;
    }
  | { kind: 'status'; id: string; quality: string; writable: boolean }
  | { kind: 'trend'; id: string; tagId: string; path: string; writable: boolean }
  | { kind: 'alarms'; id: string; title: string | null; alarms: Alarm[] }
  | { kind: 'missing'; id: string; note: string }
  | { kind: 'unreadable'; id: string; note: string };

/**
 * What one component shows, given what this session currently has (ADR-0024 §5).
 *
 * The order of the checks is the whole of the honesty rule and is deliberate:
 *
 * 1. **Not readable** — the server said this session may not see the tag. It renders as unreadable
 *    rather than disappearing, because hiding it would make a screen look complete while showing
 *    less than it was built to show, and an operator cannot know a tile is missing from a screen
 *    they did not author.
 * 2. **Readable but absent** — the server said it may be seen and this client does not have it. That
 *    is a tag that has gone, or a push that has not arrived, and either way there is nothing to
 *    print. It says so rather than showing a dash, because a dash is what a Bad reading looks like.
 * 3. **Present** — and then the value's own quality travels with it, always.
 */
export function resolveComponent(
  component: ScreenComponent,
  snapshots: ReadonlyMap<string, TagSnapshot>,
  alarms: readonly Alarm[],
  siteId: string,
): ResolvedScreenComponent {
  if (component.kind === 'label') {
    return { kind: 'label', id: component.id, text: component.title ?? '' };
  }

  if (component.kind === 'alarms') {
    // Only this screen's Site: an `alarms` component is the standing alarms of the Site the screen
    // belongs to, not of everything the session may see. Showing another Site's alarms on this
    // screen would be a summary of something the screen is not about.
    //
    // The title travels with them. It is optional (ADR-0024's kinds table gives text as what a
    // `label` shows and says nothing of the sort for `alarms`), and for a long time it was required,
    // collected by the editor and then dropped here — a heading an author had to invent and no
    // reader could ever see.
    return {
      kind: 'alarms',
      id: component.id,
      title: component.title,
      alarms: alarms.filter((alarm) => alarm.siteId === siteId),
    };
  }

  if (component.tagId === null) {
    // The server refuses this at save time, so reaching it means the row was written by something
    // other than the API. Said out loud rather than rendered as a blank.
    return { kind: 'missing', id: component.id, note: 'This component names no tag.' };
  }

  if (!component.readable) {
    return { kind: 'unreadable', id: component.id, note: 'Not available to you' };
  }

  const snapshot = snapshots.get(component.tagId);

  if (!snapshot) {
    // Nothing has arrived for it. Distinct from Bad: a Bad tag HAS a reading and it is not good.
    return { kind: 'missing', id: component.id, note: 'No reading' };
  }

  switch (component.kind) {
    case 'value':
      return {
        kind: 'value',
        id: component.id,
        // formatValue is the one place a value becomes text, and it already refuses to print a
        // number for a Bad reading — which is the guarantee every component here inherits rather
        // than re-implements.
        text: formatValue(snapshot),
        quality: snapshot.quality,
        sourceTimestampUtc: snapshot.sourceTimestampUtc,
        path: snapshot.path,
        // Marked, never actionable: writing from a screen is the next slice (ADR-0024 §9).
        writable: component.writable,
      };
    case 'status':
      return { kind: 'status', id: component.id, quality: snapshot.quality, writable: component.writable };
    case 'trend':
      return {
        kind: 'trend',
        id: component.id,
        tagId: component.tagId,
        path: snapshot.path,
        writable: component.writable,
      };
    default:
      return { kind: 'missing', id: component.id, note: 'This build cannot draw this.' };
  }
}

/**
 * A screen's components laid out as rows, in the order the renderer should consider them.
 *
 * The layout model is a row and a span of a twelve-column grid and nothing else (ADR-0024 §2), so
 * this is the whole of it: group by row, order within the row by position, and let the template put
 * the span on each cell.
 */
export function groupIntoRows(
  components: readonly ScreenComponent[],
): { rowIndex: number; components: ScreenComponent[] }[] {
  const rows = new Map<number, ScreenComponent[]>();

  for (const component of components) {
    const row = rows.get(component.rowIndex);
    if (row) {
      row.push(component);
    } else {
      rows.set(component.rowIndex, [component]);
    }
  }

  return [...rows.entries()]
    .sort(([left], [right]) => left - right)
    .map(([rowIndex, inRow]) => ({
      rowIndex,
      components: [...inRow].sort((left, right) => left.position - right.position),
    }));
}

// ---- authoring (ADR-0024's next slice) --------------------------------------
//
// The operations an author performs, here rather than inside the component that renders them, for
// the reason `resolveComponent` is here: they are decisions about a screen, and a decision made
// inside a template is one that cannot be tested without a browser.
//
// Every one of them returns a new set and rewrites rows and positions to be dense and ascending, so
// a list edited repeatedly cannot come back with a gap the renderer would draw as an empty row.

/** A component as an author sends it: no id means it is being added. */
export interface SaveScreenComponent {
  id: string | null;
  rowIndex: number;
  columnSpan: number;
  position: number;
  kind: ScreenComponentKind;
  title: string | null;
  tagId: string | null;
}

/** A screen as an author sends it: the whole component set, not the change. */
export interface SaveScreen {
  name: string;
  position: number;
  components: SaveScreenComponent[];
}

/**
 * The component as it goes back to the server.
 *
 * `readable` is deliberately dropped: it is the server's answer about the *reader*, not something an
 * author states, and sending it would invite a client that believes it can grant itself a binding.
 * A placeholder id is dropped too — for the server it means "this is new", which is what it is.
 */
export function toSaveComponent(component: ScreenComponent): SaveScreenComponent {
  return {
    id: isNew(component) ? null : component.id,
    rowIndex: component.rowIndex,
    columnSpan: component.columnSpan,
    position: component.position,
    kind: component.kind,
    title: component.title,
    tagId: component.tagId,
  };
}

/** A screen as an author sends it. */
export function toSaveScreen(screen: Screen): SaveScreen {
  return {
    name: screen.name,
    position: screen.position,
    components: screen.components.map(toSaveComponent),
  };
}

let nextId = 1;

/**
 * A component being added, before the server has given it an id.
 *
 * The id is a placeholder so a list being edited can key on it and a removal can name it, and every
 * one of them starts `new-` so `toSaveComponent` can tell it from a real one. It is not a UUID and
 * does not pretend to be: a client that invented a UUID could collide with a real row.
 */
export function newComponent(
  kind: ScreenComponentKind,
  tagId: string | null,
  title: string | null,
): ScreenComponent {
  return {
    id: `new-${nextId++}`,
    rowIndex: 0,
    columnSpan: 12,
    position: 0,
    kind,
    title,
    tagId,
    // What the reader may see is the server's to decide (ADR-0024 5). A component being authored is
    // shown as readable until a save comes back and says otherwise, because the client has no
    // standing to answer it -- and a component that hid itself mid-edit would be one an author could
    // not delete.
    readable: true,
    // False, and unlike `readable` it is NOT optimistically true. A component that has never been to
    // the server has no tag the server has looked at in this session, so there is nothing to say about
    // whether it could be written -- and the preview is the place an author compares against what an
    // operator sees, so a marker shown before a save and gone after it would be the preview lying.
    writable: false,
  };
}

/** Whether this component has been saved yet, or is still local to this edit. */
export function isNew(component: ScreenComponent): boolean {
  return component.id.startsWith('new-');
}

/**
 * The component set with one added, at the end of a row.
 *
 * Half width when the row already holds something: a full-width component beside another would be
 * twenty-four columns of a twelve-column grid, and the server refuses that — so a client that
 * defaulted to full width would produce a screen it could not save.
 */
export function addComponent(
  components: readonly ScreenComponent[],
  component: ScreenComponent,
  rowIndex: number,
): ScreenComponent[] {
  const inRow = components.filter((existing) => existing.rowIndex === rowIndex);

  return renumber([
    ...components,
    {
      ...component,
      rowIndex,
      position: inRow.length,
      columnSpan: inRow.length === 0 ? 12 : 6,
    },
  ]);
}

/** The component set with one removed. */
export function removeComponent(
  components: readonly ScreenComponent[],
  componentId: string,
): ScreenComponent[] {
  return renumber(components.filter((component) => component.id !== componentId));
}

/**
 * The component set with one changed: its span, its title, or the tag it reads.
 *
 * `undefined` means "leave this alone", because a title and a tag are both legitimately absent and
 * a caller that had to tell "set to null" from "not mentioned" would need a second parameter for
 * each. Null means set to nothing, which only a title can legally be.
 */
export function changeComponent(
  components: readonly ScreenComponent[],
  componentId: string,
  change: { columnSpan?: number; title?: string | null; tagId?: string | null },
): ScreenComponent[] {
  return components.map((component) =>
    component.id === componentId
      ? {
          ...component,
          columnSpan: change.columnSpan ?? component.columnSpan,
          title: change.title === undefined ? component.title : change.title,
          tagId: change.tagId === undefined ? component.tagId : change.tagId,
        }
      : component,
  );
}

/**
 * The component set with one inserted at a place in another row.
 *
 * The whole set is rebuilt in row order with the moved component spliced in, rather than appended
 * and trusted to sort itself out. That is because `renumber` reads array order as the order within a
 * row — which is what makes every other operation's numbering true by construction — so a component
 * appended to the array would become the last of its row whatever the caller asked for.
 */
export function moveComponent(
  components: readonly ScreenComponent[],
  componentId: string,
  rowIndex: number,
  at?: number,
): ScreenComponent[] {
  const moved = components.find((component) => component.id === componentId);

  if (!moved) {
    return [...components];
  }

  const target = components
    .filter((component) => component.id !== componentId && component.rowIndex === rowIndex)
    .sort((left, right) => left.position - right.position);

  // Where in the row: the end unless the caller said, and clamped rather than refused, because an
  // author dragging past the end of a row means the end of it.
  const place = Math.max(0, Math.min(at ?? target.length, target.length));
  target.splice(place, 0, { ...moved, rowIndex });

  const rows = [...new Set([...components.map((component) => component.rowIndex), rowIndex])].sort(
    (left, right) => left - right,
  );

  const rebuilt = rows.flatMap((row) =>
    row === rowIndex
      ? target
      : components
          .filter((component) => component.id !== componentId && component.rowIndex === row)
          .sort((left, right) => left.position - right.position),
  );

  return renumber(rebuilt);
}

/**
 * The component set with one moved a place earlier or later within its row.
 *
 * Returns the same set unchanged at either end rather than wrapping: an author pressing "left" on
 * the first component means nothing by it, and a component that jumped to the end of the row would
 * be a surprise they then have to undo.
 */
export function reorderComponent(
  components: readonly ScreenComponent[],
  componentId: string,
  direction: -1 | 1,
): ScreenComponent[] {
  const moved = components.find((component) => component.id === componentId);

  if (!moved) {
    return [...components];
  }

  const inRow = components
    .filter((component) => component.rowIndex === moved.rowIndex)
    .sort((left, right) => left.position - right.position);

  const at = inRow.findIndex((component) => component.id === componentId);
  const to = at + direction;

  if (to < 0 || to >= inRow.length) {
    return [...components];
  }

  const reordered = [...inRow];
  [reordered[at], reordered[to]] = [reordered[to], reordered[at]];

  const positions = new Map(reordered.map((component, index) => [component.id, index]));

  return components.map((component) =>
    component.rowIndex === moved.rowIndex
      ? { ...component, position: positions.get(component.id) ?? component.position }
      : component,
  );
}

/**
 * Rows and positions rewritten to be dense and ascending.
 *
 * Rows: a row that lost its last component is not a row, so the set is closed up and an author
 * never sees a gap they cannot explain.
 *
 * Positions: assigned from the order of the array within each row, which is what makes every
 * operation's "append to the end of the row" and "swap these two" true by construction rather than
 * by each operation maintaining the numbering itself. Without this a move leaves the position it
 * vacated empty — `moveComponent` hands the moved component `inTarget.length`, and the component it
 * displaced keeps the number it had — so the next render would order the row by a hole.
 */
function renumber(components: readonly ScreenComponent[]): ScreenComponent[] {
  const rows = [...new Set(components.map((component) => component.rowIndex))].sort(
    (left, right) => left - right,
  );
  const numbers = new Map(rows.map((row, index) => [row, index]));
  const withinRow = new Map<number, number>();

  return components.map((component) => {
    const row = numbers.get(component.rowIndex) ?? 0;
    const position = withinRow.get(row) ?? 0;
    withinRow.set(row, position + 1);

    return { ...component, rowIndex: row, position };
  });
}
