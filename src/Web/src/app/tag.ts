/** Wire shape of a tag value: the kind is explicit and only the matching field is set. */
export interface TagValue {
  kind: 'numeric' | 'boolean' | 'text' | 'discrete' | 'none';
  numeric?: number | null;
  boolean?: boolean | null;
  text?: string | null;
  code?: number | null;
  label?: string | null;
}

export type Quality = 'Good' | 'Uncertain' | 'Bad' | 'Stale';

/** A tag's current state, as pushed by the gateway. */
export interface TagSnapshot {
  /** Stable identity. Everything keys off this, never off the path. */
  tagId: string;
  /** Derived display label — shown to the operator, never used as a key. */
  path: string;
  value: TagValue;
  /** Null only when nothing has ever been measured for the tag. */
  sourceTimestampUtc: string | null;
  quality: Quality;
  unitSymbol: string | null;
  /** Set only on a pushed tag that has never received anything: when listening began. */
  noDataSinceUtc?: string | null;

  /**
   * The span this tag's readings are expected to fall in, and where the current one sits (ADR-0030).
   *
   * **Null is not "in range".** A deployment that declared nothing is never shown a verdict nobody
   * made — the same distinction a null on-delay carries (ADR-0025), and the reason `rangeStatus` is
   * absent rather than defaulted.
   */
  rangeLow?: number | null;
  rangeHigh?: number | null;
  rangeStatus?: 'InRange' | 'AboveRange' | 'BelowRange' | null;
}

/**
 * What to say about a reading that is outside the span its tag declares, or null when there is
 * nothing to say (ADR-0030 §5).
 *
 * **A reading the product knows is outside its range cannot be drawn exactly like one that is inside** —
 * that is ADR-0024 §5's rule about quality, extended. But the converse matters as much: a reading that
 * is *inside* gets nothing, and a tag that declared no range gets nothing. A badge saying "in range"
 * on every tile would be noise, and on a tag with no range it would be a verdict nobody made.
 *
 * It is **not** an alarm (ADR-0030 §6): no journal row, nothing to acknowledge, nothing to shelve. The
 * wording stays descriptive for that reason — "above 100" rather than "HIGH".
 */
export function outOfRangeNote(snapshot: {
  rangeStatus?: string | null;
  rangeLow?: number | null;
  rangeHigh?: number | null;
}): string | null {
  if (snapshot.rangeStatus === 'AboveRange') {
    return snapshot.rangeHigh === null || snapshot.rangeHigh === undefined
      ? 'above its declared range'
      : `above ${formatRangeEnd(snapshot.rangeHigh)}`;
  }

  if (snapshot.rangeStatus === 'BelowRange') {
    return snapshot.rangeLow === null || snapshot.rangeLow === undefined
      ? 'below its declared range'
      : `below ${formatRangeEnd(snapshot.rangeLow)}`;
  }

  // InRange, or nothing declared, or nothing measured. All three say nothing.
  return null;
}

/**
 * An end of the range, written the way a reading beside it is written.
 *
 * Two decimals like `formatMeasurement`, because the number this is compared against must not be
 * printed in a different notation from the number being compared — `above 100.00` beside `104.30`
 * reads as one scale, `above 100` beside `104.30` invites the question of whether it is the same one.
 * Trailing zeros are trimmed only when the end is whole, which is the common case for a declared span.
 */
function formatRangeEnd(value: number): string {
  return Number.isInteger(value) ? value.toString() : value.toFixed(2);
}

/**
 * Renders a value for display. A value whose quality is not Good has no number worth
 * showing: the point of the quality field is that "no reading" is not zero.
 */
export function formatValue(snapshot: TagSnapshot): string {
  if (snapshot.quality === 'Bad') {
    return '—';
  }

  const value = snapshot.value;
  if (!value) {
    return '—';
  }

  switch (value.kind) {
    case 'numeric':
      return value.numeric == null || Number.isNaN(value.numeric)
        ? '—'
        : value.numeric.toFixed(2);
    case 'boolean':
      return value.boolean ? 'ON' : 'OFF';
    case 'text':
      return value.text ?? '—';
    case 'discrete':
      return value.label ?? String(value.code ?? '—');
    default:
      return '—';
  }
}
