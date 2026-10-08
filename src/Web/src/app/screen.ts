import { Alarm, TrendBucket } from './models';
import { formatValue, outOfRangeNote, Quality, TagSnapshot, TagValue } from './tag';

/** The component kinds this build renders. The server refuses any other (ADR-0024 §3). */
export type ScreenComponentKind = 'label' | 'value' | 'trend' | 'alarms' | 'status' | 'symbol';

/**
 * The symbols this build can draw (ADR-0027), and the states each can be drawn in.
 *
 * Duplicated from Core's `Symbols`, and that duplication is deliberate rather than careless — see
 * `deriveSymbolState` below for why the evaluation lives here as well as there, and this is the same
 * decision: a drawing's states are the drawing's, and the drawing is this file's.
 */
export const SYMBOL_STATES = {
  pump: ['running', 'stopped', 'fault', 'unknown', 'bad', 'stale'],
  motor: ['running', 'stopped', 'fault', 'unknown', 'bad', 'stale'],
  valve: ['open', 'closed', 'fault', 'unknown', 'bad', 'stale'],
  // **Bands, never a fill that tracks the reading** (ADR-0027 §5). The author says where low ends and
  // high begins with the same comparisons every other symbol uses; the drawing shows which band the
  // reading fell in. A proportional fill would be a continuing value driving a picture, and a reader
  // cannot measure a height.
  tank: ['low', 'normal', 'high', 'unknown', 'bad', 'stale'],
} as const;

export type SymbolShape = keyof typeof SYMBOL_STATES;

/** Whether a mapping rule's comparison is one this build evaluates. */
export const SYMBOL_COMPARISONS = ['equals', 'above', 'below'] as const;

/**
 * One rule of a symbol's mapping (ADR-0027): draw `state` when the reading is this.
 *
 * `otherwise` marks the fallback, which matches anything and is used only when nothing else did.
 */
export interface SymbolStateRule {
  state: string;
  when: string | null;
  value: string | null;
  otherwise: boolean;
}

/** One thing on a screen, as the API sends it. */
export interface ScreenComponent {
  id: string;
  rowIndex: number;
  columnSpan: number;
  position: number;
  kind: ScreenComponentKind;
  title: string | null;
  tagId: string | null;
  /** Which drawing, for a `symbol`. Null for every other kind (ADR-0027). */
  symbol: SymbolShape | null;
  /** How to derive a symbol's state from its tag. Empty for every other kind. */
  states: readonly SymbolStateRule[];
  /**
   * Whether this session may see the value behind `tagId`.
   *
   * Decided by the server, not here, so that every renderer gives the same answer and there is one
   * place to check (ADR-0024 §5). Always true for a component that reads no tag.
   */
  readable: boolean;
  /**
   * Whether this session could write the tag behind `tagId` (ADR-0026 §2).
   *
   * Decided by the server for the same reason `readable` is, and false for a reader who cannot
   * operate the Site even when the tag itself is writable.
   */
  writable: boolean;
}

/**
 * What a symbol should be drawn as, and why (ADR-0027).
 */
export interface DerivedSymbolState {
  /** One of the symbol's own states. Never null: there is always something to draw. */
  state: string;
  /** Whether it came from the reading's quality rather than from the mapping (ADR-0027 §4). */
  fromQuality: boolean;
  /** Which rule decided it, or null when quality did or nothing matched. For an author debugging. */
  matchedRule: number | null;
}

/**
 * Derive a symbol's state from its reading (ADR-0027).
 *
 * **This is evaluated in the browser, and the same rules are evaluated in Core's `SymbolStates` for
 * the tests. That duplication is a decision, not an oversight**, and here is the argument for it.
 *
 * The alternative was to have the server send the derived state with each screen read. It cannot:
 * a screen is fetched once and its readings arrive afterwards and continuously over the hub, so the
 * server has no moment at which it knows both. A per-read change stream would be a second push
 * channel carrying a derived value — more moving parts than the rule it would save, and it would make
 * a screen's appearance depend on a subscription having arrived.
 *
 * What makes the duplication tolerable is that **the shape of the mapping is fixed by the API**, so
 * the two implementations cannot drift in what they accept: the server refuses a comparison it does
 * not evaluate (ADR-0027's consequences), which means this one only ever sees `equals`, `above` and
 * `below`. The two are cross-checked by the client's own tests naming the same cases as the Core ones
 * — including the one that matters most, that quality wins over the value.
 *
 * **Quality before mapping** (§4): a reading that is not Good has no state, whatever it says. A boolean
 * tag that has gone Bad still carries its last value, and a pump drawn as running because of a reading
 * nothing measured is the failure ADR-0003 exists to prevent — in the medium where it is hardest to
 * notice, because a turning pump looks like news rather than like a missing reading.
 */
export function deriveSymbolState(
  rules: readonly SymbolStateRule[],
  reading: { value: TagValue; quality: Quality } | null,
): DerivedSymbolState {
  if (reading === null) {
    return { state: 'unknown', fromQuality: false, matchedRule: null };
  }

  if (reading.quality !== 'Good') {
    return {
      state: reading.quality === 'Bad' ? 'bad' : 'stale',
      fromQuality: true,
      matchedRule: null,
    };
  }

  let fallback: { rule: SymbolStateRule; at: number } | null = null;

  for (let at = 0; at < rules.length; at++) {
    const rule = rules[at];

    if (rule.otherwise) {
      // Remembered, not taken: a fallback matches anything, so honouring it where it stands would
      // make every rule after it dead. Used only if nothing else matched.
      fallback ??= { rule, at };
      continue;
    }

    if (matchesRule(rule, reading.value)) {
      return { state: rule.state, fromQuality: false, matchedRule: at };
    }
  }

  return fallback === null
    ? { state: 'unknown', fromQuality: false, matchedRule: null }
    : { state: fallback.rule.state, fromQuality: false, matchedRule: fallback.at };
}

/**
 * Whether one rule matches a reading.
 *
 * A comparison this build does not know is **not a match** rather than an error: the mapping is data,
 * a screen is read by everyone, and a rule that cannot be evaluated should cost its own state rather
 * than the screen. The API refuses one at save time, so this is the second line.
 */
function matchesRule(rule: SymbolStateRule, value: TagValue): boolean {
  if (rule.when === 'equals') {
    return equalsValue(value, rule.value);
  }

  // Above and below are numeric only: a boolean has no ordering, and a text tag's is not one an
  // operator would expect a symbol to be drawn from.
  if (value.kind !== 'numeric' || typeof value.numeric !== 'number' || rule.value === null) {
    return false;
  }

  const threshold = parseInvariantNumber(rule.value);

  // A threshold that is not a finite number never matches, and Core's `SymbolStates.Matches` says the
  // same. `ScreenRules` refuses such a rule at save, so this is the second line — and it has to exist,
  // because the editor's live preview evaluates a draft that has not been near the server.
  if (threshold === null || !Number.isFinite(threshold)) {
    return false;
  }

  return rule.when === 'above'
    ? value.numeric > threshold
    : rule.when === 'below'
      ? value.numeric < threshold
      : false;
}

/**
 * What an author typed, read as a number the way **Core reads it** — or null when it is not one.
 *
 * **This exists because `Number()` and .NET's `double.TryParse` disagree, and the disagreement was
 * reachable.** ADR-0027 has the same rules evaluated in two languages, and the argument for tolerating
 * that is that both sides are held to the same cases. Three cases where they were not, measured on
 * 2026-10-07 rather than assumed:
 *
 * | typed      | `double.TryParse(…, Float, Invariant)` | `Number()`        |
 * |------------|----------------------------------------|-------------------|
 * | `""`       | refused                                | **0**             |
 * | `"   "`    | refused                                | **0**             |
 * | `"0x10"`   | refused                                | **16**            |
 *
 * The empty one was not theoretical. The editor's `addRule` creates a rule with an empty value, and
 * **the live preview evaluates the draft immediately** — so an author adding a state to a symbol bound
 * to a tag reading `0.00` saw the preview jump into that state, and then the save was refused because
 * the API will not store a comparison with nothing to compare against. The preview's whole purpose is
 * to show what an operator will see, and it was showing a state the product cannot produce.
 *
 * So: a decimal number, with an optional sign, an optional exponent and surrounding space — and
 * nothing else. `Infinity` and `NaN` are accepted because .NET accepts them, and they are then handled
 * exactly as .NET handles them: `NaN` equals nothing, and a non-finite **threshold** never matches on
 * either side (`SymbolStates.Matches` has the same guard, and `ScreenRules` refuses one at save).
 */
function parseInvariantNumber(typed: string | null): number | null {
  if (typed === null) {
    return null;
  }

  // `trim()` removes the same Unicode whitespace .NET's AllowLeadingWhite / AllowTrailingWhite do.
  const text = typed.trim();

  if (/^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$/.test(text)) {
    return Number(text);
  }

  if (/^[+-]?Infinity$/.test(text)) {
    return text.startsWith('-') ? -Infinity : Infinity;
  }

  return text === 'NaN' ? Number.NaN : null;
}

/**
 * Whether a reading's value equals what an author typed.
 *
 * Compared by parsing rather than by string equality, so that `4.50` and `4.5` are the same threshold
 * and `True` and `true` are the same boolean — an author lining a form up should not thereby create a
 * state nothing reaches. This mirrors Core's `SymbolStates.Equals` case for case, including its
 * case-insensitive text comparison.
 */
function equalsValue(value: TagValue, typed: string | null): boolean {
  if (typed === null) {
    return false;
  }

  switch (value.kind) {
    case 'numeric': {
      const wanted = parseInvariantNumber(typed);
      return wanted !== null && value.numeric === wanted;
    }
    case 'boolean':
      return String(value.boolean).toLowerCase() === typed.trim().toLowerCase();
    case 'text':
      return (value.text ?? '').toLowerCase() === typed.toLowerCase();
    default:
      return false;
  }
}

/**
 * Which of a symbol's rules can never match a tag of this kind, and why (ADR-0027).
 *
 * **This exists because of one defect, and it is worth stating what the defect was.** A new symbol was
 * born with `running when the value is true` and `stopped otherwise`, which is right for a pump driven
 * by a boolean and silently wrong for every other tag: a number never compares equal to `true`, so the
 * fallback is taken for every reading the tag will ever report and the symbol draws `stopped` forever.
 * **A fallback that is always taken is the dangerous shape** — it does not look broken, it looks like a
 * working symbol reporting a stopped machine, which is a claim about a plant that nothing measured.
 *
 * ADR-0027 §3 designed `unknown` to be the visible sign that a mapping does not cover a reading. A
 * fallback replaces that sign with a definite state, which is the author's right — so the product
 * cannot call this an error. What it can do is make the mistake hard to make (a default that follows
 * the tag, in `newComponent`) and easy to see (this, in the editor).
 *
 * **Why this is not a save-time refusal.** It would need the tag's kind in `ScreenRules`, and a stored
 * screen would stop being saveable the moment somebody changed a tag's kind underneath it. The one part
 * that needs no tag — a numeric comparison against something that is not a number — *is* refused on the
 * server, and that split is recorded in `ScreenRules` itself.
 *
 * A kind of `none` means nothing has been measured yet, and then **nothing is known**: the answer is no
 * warnings rather than a guess, because warning about a rule that may well be right is how a reader
 * learns to ignore warnings.
 */
export function unmatchableRules(
  rules: readonly SymbolStateRule[],
  kind: TagValue['kind'],
): readonly { at: number; reason: string }[] {
  if (kind === 'none') {
    return [];
  }

  const found: { at: number; reason: string }[] = [];

  for (let at = 0; at < rules.length; at++) {
    const rule = rules[at];

    // A fallback matches anything by definition, so it is never unmatchable. Whether it is the *right*
    // thing to draw when nothing matched is exactly the judgement this function must not make.
    if (rule.otherwise) {
      continue;
    }

    const reason = whyUnmatchable(rule, kind);

    if (reason !== null) {
      found.push({ at, reason });
    }
  }

  return found;
}

/**
 * Why one rule can never match a reading of this kind, or null when it could.
 *
 * Every arm mirrors a branch of `matchesRule` and `equalsValue` above, and it has to stay that way:
 * this function claims a rule is dead, and a claim like that is only as true as its agreement with the
 * code that actually evaluates the rule. A `discrete` reading is the blunt case — `equalsValue` has no
 * arm for it, so no comparison of any kind can match one, which is worth saying plainly rather than
 * leaving an author to discover it from a symbol that never changes.
 */
function whyUnmatchable(rule: SymbolStateRule, kind: TagValue['kind']): string | null {
  if (kind === 'discrete') {
    return 'this tag reports a discrete code, and a symbol cannot compare against one yet';
  }

  if (rule.when === 'above' || rule.when === 'below') {
    if (kind !== 'numeric') {
      return `'${rule.when}' compares numbers, and this tag reports ${describeKind(kind)}`;
    }

    const threshold = parseInvariantNumber(rule.value);

    return threshold !== null && Number.isFinite(threshold)
      ? null
      : `'${rule.value}' is not a number to be ${rule.when}`;
  }

  if (rule.when !== 'equals') {
    // A comparison this build does not evaluate. The API refuses one at save, so an author should
    // never see this — it is here because `matchesRule` treats it as "no match", and a rule that can
    // never match is what this function is for.
    return `this build does not evaluate '${rule.when}'`;
  }

  const typed = (rule.value ?? '').trim();

  if (kind === 'numeric') {
    return parseInvariantNumber(typed) !== null
      ? null
      : `this tag reports a number, and '${rule.value}' is not one`;
  }

  if (kind === 'boolean') {
    const lowered = typed.toLowerCase();
    return lowered === 'true' || lowered === 'false'
      ? null
      : `this tag reports true or false, and '${rule.value}' is neither`;
  }

  // Text. Any string is something a text reading could equal, so there is nothing to warn about —
  // including the empty string, which the API refuses at save for its own reasons.
  return null;
}

/** How to name a value kind in a sentence an author reads. */
function describeKind(kind: TagValue['kind']): string {
  switch (kind) {
    case 'numeric':
      return 'a number';
    case 'boolean':
      return 'true or false';
    case 'text':
      return 'text';
    case 'discrete':
      return 'a discrete code';
    default:
      return 'nothing yet';
  }
}

/**
 * The mapping a new symbol starts with, for the kind of tag it is being bound to (ADR-0027).
 *
 * **The default follows the tag because the old fixed one was a defect**, not because it is tidier: see
 * `unmatchableRules`. Every default here is a mapping whose rules *can* match the tag — never a
 * guarantee that it is the mapping the plant wants, which no code can know.
 *
 * A tag with no reading yet still gets the boolean default, and that is a deliberate bet rather than an
 * oversight: most pumps are driven by a run signal, the author sees the mapping in the form in front of
 * them, and the warning appears as soon as a reading arrives and disagrees. Starting such a symbol on a
 * bare `unknown` fallback would make the common case worse to protect the uncommon one.
 */
function defaultSymbolStates(shape: SymbolShape, kind: TagValue['kind']): SymbolStateRule[] {
  const states = SYMBOL_STATES[shape];

  // Nothing to guess from a tag that reports text, a discrete code, or nothing yet: a lone fallback is
  // saveable, draws ADR-0027 §3's own "the mapping does not cover this" state, and says to the author
  // that it is theirs to fill in. It is not a mapping pretending to work.
  const admitNothing: SymbolStateRule[] = [
    { state: 'unknown', when: null, value: null, otherwise: true },
  ];

  if (kind === 'text' || kind === 'discrete') {
    return admitNothing;
  }

  if (shape === 'tank') {
    // A tank reads a level, so a boolean tells it nothing and it says so rather than inventing a band.
    if (kind !== 'numeric') {
      return admitNothing;
    }

    // **Bands the author will move, not bands the product believes.** Twenty and eighty are where a
    // reader expects "nearly empty" and "nearly full" on a percentage, which is what most level tags
    // are; on a tag in metres they are wrong and visibly wrong, which is the point — a default that
    // looked plausible on every unit would be one nobody checked.
    return [
      { state: 'high', when: 'above', value: '80', otherwise: false },
      { state: 'low', when: 'below', value: '20', otherwise: false },
      { state: 'normal', when: null, value: null, otherwise: true },
    ];
  }

  // A pump, a motor or a valve: on or off, under whichever pair of names this drawing uses.
  const on = states[0];
  const off = states[1];

  if (kind === 'numeric') {
    // Above zero rather than `is 1`: a run or open signal arriving as a number is a 0/1, a 0/100 or a
    // position, and "above zero" is the one reading of it that is true in all three.
    return [
      { state: on, when: 'above', value: '0', otherwise: false },
      { state: off, when: null, value: null, otherwise: true },
    ];
  }

  return [
    { state: on, when: 'equals', value: 'true', otherwise: false },
    { state: off, when: null, value: null, otherwise: true },
  ];
}

/**
 * The mapping to keep when an author changes a symbol's drawing.
 *
 * **A symbol is a drawing plus a state list, and the list belongs to the drawing.** Changing a pump
 * into a valve leaves rules naming `running` and `stopped`, which a valve cannot be drawn in — so the
 * server refuses the save, by name, about rules the author never wrote. That is the shape of defect
 * this project has found in four walks: a rule and every place that applies it have to move together.
 *
 * So the mapping is kept **exactly** when every state it names survives the change — pump to motor is
 * the same six states, and an author who has tuned thresholds keeps them — and replaced with the new
 * drawing's default when it does not. Replacing loses work, and it is still the honest answer: the
 * rules named states that no longer exist, and remapping `running` onto `open` would be the product
 * deciding what an author meant.
 */
export function remapForShape(
  states: readonly SymbolStateRule[],
  shape: SymbolShape,
  kind: TagValue['kind'],
): SymbolStateRule[] {
  const drawable: readonly string[] = SYMBOL_STATES[shape];

  return states.every((rule) => drawable.includes(rule.state))
    ? [...states]
    : defaultSymbolStates(shape, kind);
}

/**
 * The tags whose history a set of components needs, each once.
 *
 * Extracted from the app component so it is pure and testable, and so the read view and the
 * editor's preview agree about what a trend needs **by construction** rather than by two filters
 * that happen to look alike today — the shape of defect Phase 8's walk found twice.
 *
 * Three exclusions, each for its own reason:
 *
 * - **not a `trend`**: nothing else draws a series.
 * - **not `readable`**: this session may not see the tag, so asking the server for its history is a
 *   request that can only fail — and the component says "unreadable" rather than "Reading…".
 * - **no `tagId`**: a trend an author has added but not yet bound to anything. Asking for history
 *   for `null` is not a question the API has an answer to.
 *
 * De-duplicated because two trend components bound to one tag are two views of one series, and one
 * fetch serves both.
 */
export function trendTagIds(components: readonly ScreenComponent[]): readonly string[] {
  return [
    ...new Set(
      components
        .filter((component) => component.kind === 'trend' && component.readable && component.tagId)
        .map((component) => component.tagId!),
    ),
  ];
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
      /**
       * The tag behind this component, which is what a write is addressed to (ADR-0026).
       *
       * Carried rather than looked up again from the component, so that the thing the dialog writes
       * to is the thing whose reading is on screen beside it.
       */
      tagId: string;
      /** What a new value has to be. The server decides this too, and refuses a mismatch. */
      valueKind: TagValue['kind'];
      text: string;
      quality: string;
      /** Whether this is an exact zero. Marked, not judged — many tags sit at zero and mean it. */
      zero: boolean;
      /**
       * What the tile is called: the author's `title` if they set one, otherwise the tag's own name.
       *
       * Not the full display path. That is right for a tree, where a reader picks between tags with
       * the same name under different devices; on a screen built for one Site it is mostly
       * repetition, and at 1920 it wrapped onto a second line in every tile.
       */
      caption: string;
      /** When this reading stopped being fresh, or null when it is fresh and Good. */
      note: string | null;
      sourceTimestampUtc: string | null;
      path: string;
      /**
       * Whether this session could write this tag, decided by the server (ADR-0024 §5, ADR-0026 §2).
       *
       * False for a kind that reads no tag, for a tag that is not writable, for a tag this session
       * may not see, and for a reader who cannot operate the Site. **What it gates is the offer of a
       * control, not the write itself** — the API refuses an unpermitted write whatever a client
       * draws. See ADR-0026 §2 for why the flag is still worth having.
       */
      writable: boolean;
      /**
       * What to say about a reading outside the span its tag declares, or null (ADR-0030 §5).
       *
       * Null covers three different silences on purpose: the reading is inside its range, the tag
       * declared none, or nothing has been measured. **None of them is a verdict**, and a tile that
       * said "in range" on every other reading would teach a reader to stop looking at the marker that
       * matters.
       */
      rangeNote: string | null;
    }
  | { kind: 'status'; id: string; quality: string; writable: boolean }
  | {
      kind: 'symbol';
      id: string;
      tagId: string;
      /** Which drawing. */
      shape: SymbolShape;
      /**
       * The state to draw, from the mapping and the reading (ADR-0027).
       *
       * Always one of the shape's states, so the renderer needs no fallback of its own.
       */
      state: string;
      /**
       * Whether the state came from the reading's quality rather than from the mapping.
       *
       * The drawing does not need it — `bad` and `stale` are drawn as themselves — but a screen whose
       * pump says "no reading" is answering a question an operator will ask, so it is carried rather
       * than re-derived in the template.
       */
      fromQuality: boolean;
      /** The reading as text, for the caption. */
      caption: string;
      path: string;
    }
  | { kind: 'trend'; id: string; tagId: string; path: string; caption: string; writable: boolean }
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
        tagId: component.tagId,
        valueKind: snapshot.value.kind,
        // formatValue is the one place a value becomes text, and it already refuses to print a
        // number for a Bad reading — which is the guarantee every component here inherits rather
        // than re-implements.
        text: formatValue(snapshot),
        quality: snapshot.quality,
        // A zero is the reading most easily missed and often the one that matters, so it is marked
        // rather than left to be spotted by comparing digits.
        zero: isZeroReading(snapshot),
        // The short label, and the time only when it is worth reading. Both are decisions about a
        // screen rather than formatting, which is why they are here and tested without a browser.
        caption: component.title?.trim() || tagNameOf(snapshot.path),
        note: stalenessNote(snapshot.quality, snapshot.sourceTimestampUtc),
        sourceTimestampUtc: snapshot.sourceTimestampUtc,
        path: snapshot.path,
        // Marks the control, and the write is refused again by the API if anyone gets here without
        // the role (ADR-0026 §2).
        writable: component.writable,
        // ADR-0024 §5 says every component that reads a tag shows that tag's quality; ADR-0030 §5
        // extends it: a reading the product knows is outside its declared range cannot be drawn
        // exactly like one that is inside.
        rangeNote: outOfRangeNote(snapshot),
      };
    case 'status':
      return { kind: 'status', id: component.id, quality: snapshot.quality, writable: component.writable };
    case 'symbol': {
      // A symbol with no shape, or one this build does not draw, is `missing` rather than a blank: the
      // server refuses both at save time, so reaching here means the row was written another way.
      const shape = component.symbol;

      if (shape === null || !(shape in SYMBOL_STATES)) {
        return { kind: 'missing', id: component.id, note: 'This build cannot draw this symbol.' };
      }

      const derived = deriveSymbolState(component.states, {
        value: snapshot.value,
        quality: snapshot.quality,
      });

      return {
        kind: 'symbol',
        id: component.id,
        tagId: component.tagId,
        shape,
        state: derived.state,
        fromQuality: derived.fromQuality,
        // The reading as text beside the picture, because a picture of a pump says whether it runs
        // and not what it is reading -- and on a malfunctioning plant those are two different facts.
        caption: `${component.title?.trim() || tagNameOf(snapshot.path)}: ${formatValue(snapshot)}`,
        path: snapshot.path,
      };
    }
    case 'trend':
      return {
        kind: 'trend',
        id: component.id,
        tagId: component.tagId,
        path: snapshot.path,
        // The same short caption the value tiles use, for the same reason: the trend sits beside
        // them and a repeated device path on one and not the others reads as two different things.
        caption: component.title?.trim() || tagNameOf(snapshot.path),
        writable: component.writable,
      };
    default:
      return { kind: 'missing', id: component.id, note: 'This build cannot draw this.' };
  }
}

/**
 * The last segment of a display path — a tag's own name.
 *
 * `Skopje/Pump House/Discharge Pressure` becomes `Discharge Pressure`, and that is what a tile is
 * captioned with. The full path is right for a tree, where a reader is choosing between tags with
 * the same name under different devices; on a screen built for one Site it is three quarters
 * repetition, and at 1920 it wrapped onto a second line in every tile — found by looking at
 * 2026-10-06. A component's own `title`, when an author sets one, wins over this.
 */
export function tagNameOf(path: string): string {
  const trimmed = path.trim();
  const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'));
  return cut >= 0 ? trimmed.slice(cut + 1).trim() : trimmed;
}

/**
 * Whether a reading's age is worth putting on the tile, and what to say.
 *
 * **A timestamp on every tile is noise, and on a screen it is noise that repeats.** What a reader
 * needs to know is when a reading stopped being trustworthy, so this returns null for a fresh Good
 * reading and the time otherwise — which makes the presence of a time on one tile the signal, rather
 * than its absence meaning nothing. The threshold is deliberately generous: a scan every second and
 * a clock on a wall do not need reconciling, but half an hour does.
 *
 * A Bad reading's own source time is often the last time anything arrived, which is exactly the fact
 * worth showing, so it is shown whether or not it is old.
 */
export function stalenessNote(
  quality: string,
  sourceTimestampUtc: string | null,
  now: Date = new Date(),
  staleAfterMs = 5 * 60 * 1000,
): string | null {
  if (quality === 'Bad') {
    return sourceTimestampUtc === null ? 'no reading' : `last at ${clockTime(sourceTimestampUtc)}`;
  }

  if (sourceTimestampUtc === null) {
    return null;
  }

  const measured = new Date(sourceTimestampUtc).getTime();

  if (Number.isNaN(measured)) {
    return null;
  }

  return now.getTime() - measured > staleAfterMs ? `at ${clockTime(sourceTimestampUtc)}` : null;
}

/** An ISO timestamp as the wall clock reads it, with no date and no seconds — a screen is not a log. */
function clockTime(iso: string): string {
  const at = new Date(iso);

  return Number.isNaN(at.getTime())
    ? iso
    : `${String(at.getHours()).padStart(2, '0')}:${String(at.getMinutes()).padStart(2, '0')}`;
}

/**
 * Whether a trend's history has stopped growing, and what to say about it (ADR-0003).
 *
 * **This is a different question from a reading's quality, and the difference is the defect it fixes.**
 * A trend draws only Good numeric samples, which is right — a Bad sample carries no value to plot. But
 * that means **a trend is not stopped by an outage, it is frozen by one**: the line keeps its shape,
 * the reading count keeps its number, and the tile goes on looking like a plant that is running. Found
 * on 2026-10-06 by making a whole Site go Bad and looking at the screen: every value tile said `BAD`
 * and showed a dash, and the trend beside them was unchanged.
 *
 * It is **the same failure Phase 1's walk found in this same chart** — a line drawn through a
 * Gateway-downtime gap, which made an outage look like steady data — in its second form. Then the lie
 * was a straight line; now it is an old line left on screen.
 *
 * **The test is the newest sample's age, not the tag's reading quality**, and that distinction is the
 * whole design. A trend can be stale while its tag reads Good — a driver that stops delivering
 * without saying so — and that is the case nothing else on the screen catches. A trend with no
 * samples at all is not stale, it is empty, and the chart already says so.
 *
 * Rendered by the view rather than resolved into the component, because **age is a fact about now**:
 * the resolver runs when a screen loads and the answer would be frozen at that moment, which is
 * precisely the mistake being fixed.
 *
 * **It reads `lastUtc`, the newest reading in each bucket, and it has to** (ADR-0029 §4): a bucket's
 * `startUtc` is the edge of the stretch rather than a measurement time, so an age decided on it would
 * name a time nothing was measured at in the note below, and would declare a live trend stale up to
 * one bucket early — seventeen minutes early on a seven-day window.
 */
export function trendFreshness(
  buckets: readonly TrendBucket[],
  now: Date = new Date(),
  staleAfterMs = 5 * 60 * 1000,
): { stale: boolean; note: string | null } {
  if (buckets.length === 0) {
    return { stale: false, note: null };
  }

  // The newest reading, not the last bucket in the array: history comes back in whatever order the
  // API chose, so position says nothing about time — a newest-first array would make a fresh trend
  // look stale, and an oldest-first one would do the reverse. The maximum of `lastUtc` depends on the
  // readings and not on the order they arrived in.
  const newest = buckets.reduce((latest, bucket) =>
    new Date(bucket.lastUtc).getTime() > new Date(latest.lastUtc).getTime() ? bucket : latest,
  );

  const measured = new Date(newest.lastUtc).getTime();

  if (Number.isNaN(measured)) {
    return { stale: false, note: null };
  }

  return now.getTime() - measured > staleAfterMs
    ? { stale: true, note: `no reading since ${clockTime(newest.lastUtc)}` }
    : { stale: false, note: null };
}

/**
 * Whether a reading is exactly zero, for a numeric tag.
 *
 * A zero is the reading most easily missed and the one most often worth noticing — a flow that has
 * stopped, a level that has emptied. Marking it is **not** a judgement about whether zero is wrong:
 * many tags sit at zero all day and mean it. It is the same rule as the `noDataSince` note and the
 * quality pill, that a reading which is unusual for a screen should not have to be spotted by
 * comparing digits.
 *
 * Only for a Good numeric reading. A Bad one has no number to be zero (ADR-0003), and a boolean
 * false is a different fact that the caption already says.
 */
export function isZeroReading(snapshot: { quality: string; value: { kind: string; numeric?: number | null } }): boolean {
  return snapshot.quality === 'Good' && snapshot.value.kind === 'numeric' && snapshot.value.numeric === 0;
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
  symbol: SymbolShape | null;
  states: readonly SymbolStateRule[];
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
    // Sent for every kind, and empty rather than absent for the kinds that have none. The API treats
    // a missing list as empty anyway, so this is belt-and-braces against the one shape that would
    // matter: a `symbol` whose mapping was dropped on the way out would be refused as having no
    // states, and the author would be told their work was never there.
    symbol: component.symbol,
    states: component.states,
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
  /**
   * What the tag being bound is currently reading, for a `symbol`'s starting mapping.
   *
   * Defaulted so every other kind can ignore it, and `'none'` is the honest value for a tag nothing
   * has measured — `defaultSymbolStates` says what it does with that and why.
   */
  valueKind: TagValue['kind'] = 'none',
): ScreenComponent {
  return {
    id: `new-${nextId++}`,
    rowIndex: 0,
    columnSpan: 12,
    position: 0,
    kind,
    title,
    tagId,
    // A new symbol starts as a pump with a mapping it can be saved with. That is a decision rather
    // than a default — the API refuses a symbol with no states, so "start empty and let the author
    // fill it in" would hand them a component that cannot be saved until they have understood the
    // mapping — and the mapping now **follows the kind of tag being bound**, because a fixed one was
    // right for a boolean and silently wrong for everything else. `defaultSymbolStates` has the
    // argument and `unmatchableRules` has the defect it came from.
    //
    // **The fallback carries no comparison**, which the API insists on: a rule that matches anything
    // cannot also state what it matches, and one that did would be text the author believed was doing
    // something.
    symbol: kind === 'symbol' ? 'pump' : null,
    states: kind === 'symbol' ? defaultSymbolStates('pump', valueKind) : [],
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
 * Where a drop lands, as a row and a place in it, or null when the drop means nothing.
 *
 * **This exists because the index an author sees and the index `moveComponent` takes are not the
 * same index**, and the difference is invisible until it is wrong. `moveComponent` builds its target
 * row with the moved component already taken out — that is what lets it treat a move within a row and
 * a move between rows as one operation — so `at` counts places in a row that no longer contains the
 * thing being moved. A drop handler reading the rendered row, which *does* contain it, is one too high
 * for every drop to the right of where the component started.
 *
 * So the editor passes ids and a side, and the arithmetic is done here where it can be tested without
 * a browser: `side` is which half of the target the pointer was over, and the whole of dragging is
 * which component you let go on and which side of it.
 *
 * Null for a drop that cannot mean anything — onto itself, or onto something no longer in the set.
 * The caller does nothing with a null rather than guessing, because a guess here moves a component an
 * author did not ask to move.
 */
export function dropPosition(
  components: readonly ScreenComponent[],
  draggedId: string,
  ontoId: string,
  side: 'before' | 'after',
): { rowIndex: number; at: number } | null {
  if (draggedId === ontoId) {
    return null;
  }

  const onto = components.find((component) => component.id === ontoId);

  // Both ends have to be real. Only `onto` being checked would leave a drag of something that is no
  // longer in the set answering with a position — and `moveComponent` would then do nothing with it,
  // so the editor would redraw, renumber and report a change that did not happen.
  if (!onto || !components.some((component) => component.id === draggedId)) {
    return null;
  }

  // The same frame `moveComponent` will build: this row, without the component being dragged.
  const row = components
    .filter((component) => component.rowIndex === onto.rowIndex && component.id !== draggedId)
    .sort((left, right) => left.position - right.position);

  const at = row.findIndex((component) => component.id === ontoId);

  return at === -1 ? null : { rowIndex: onto.rowIndex, at: side === 'before' ? at : at + 1 };
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
