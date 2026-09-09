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
  sourceTimestampUtc: string;
  quality: Quality;
  unitSymbol: string | null;
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
