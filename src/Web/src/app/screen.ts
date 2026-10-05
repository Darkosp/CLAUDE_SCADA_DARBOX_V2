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
    }
  | { kind: 'status'; id: string; quality: string }
  | { kind: 'trend'; id: string; tagId: string; path: string }
  | { kind: 'alarms'; id: string; alarms: Alarm[] }
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
    return { kind: 'alarms', id: component.id, alarms: alarms.filter((alarm) => alarm.siteId === siteId) };
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
      };
    case 'status':
      return { kind: 'status', id: component.id, quality: snapshot.quality };
    case 'trend':
      return { kind: 'trend', id: component.id, tagId: component.tagId, path: snapshot.path };
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
