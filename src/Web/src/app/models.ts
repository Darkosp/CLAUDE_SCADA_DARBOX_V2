/** A unit as the gateway sends it: a dimension and its conversion to SI, not a bare symbol. */
export interface Unit {
  symbol: string;
  dimension: string;
  factorToSi: number;
  offsetToSi: number;
}

export interface TreeTag {
  id: string;
  name: string;
  valueKind: 'Numeric' | 'Boolean' | 'Text' | 'Discrete';
  unit: Unit | null;
  sourceAddress: string;
  isWritable: boolean;
}

export interface TreeDevice {
  id: string;
  name: string;
  driverKey: string;
  connectionSettings: Record<string, string>;
  scanIntervalMs: number;
  folderId: string | null;
  tags: TreeTag[];
}

export interface TreeFolder {
  id: string;
  name: string;
  parentFolderId: string | null;
  folders: TreeFolder[];
  devices: TreeDevice[];
}

export interface SiteTree {
  siteId: string;
  name: string;
  timeZoneId: string;
  folders: TreeFolder[];
  devices: TreeDevice[];
}

export interface Site {
  id: string;
  name: string;
  timeZoneId: string;
}

export interface HistorySample {
  value: {
    kind: string;
    numeric?: number | null;
    boolean?: boolean | null;
    text?: string | null;
    code?: number | null;
    label?: string | null;
  };
  sourceTimestampUtc: string;
  ingestedAtUtc: string;
  quality: string;
}

/** A device type, instantiated many times (ADR-0010). */
export interface DeviceTemplate {
  id: string;
  name: string;
}

/** One tag on a template, with the parameters its address needs. */
export interface TemplateTag {
  id: string;
  name: string;
  valueKind: 'Numeric' | 'Boolean' | 'Text' | 'Discrete';
  unit: Unit | null;
  addressTemplate: string;
  isWritable: boolean;
  parameterNames: string[];
}

/** A connection setting as the UI edits it — a name and a value, nothing driver-specific. */
export interface SettingEntry {
  key: string;
  value: string;
}

/** Turns the editable pairs back into the map the API expects, dropping blank names. */
export function settingsToMap(entries: SettingEntry[]): Record<string, string> {
  const map: Record<string, string> = {};
  for (const entry of entries) {
    if (entry.key.trim().length > 0) {
      map[entry.key.trim()] = entry.value;
    }
  }
  return map;
}

export function mapToSettings(map: Record<string, string>): SettingEntry[] {
  return Object.entries(map).map(([key, value]) => ({ key, value }));
}

export type AlarmState = 'Active' | 'Acknowledged' | 'Cleared' | 'Shelved';

/** One standing alarm, as pushed by the gateway. */
export interface Alarm {
  /** This alarm, as distinct from earlier and later ones on the same definition. */
  occurrenceId: string;
  definitionId: string;
  tagId: string;
  /** The Site the alarm was raised in, fixed at the raise. */
  siteId: string;
  tagPath: string;
  limit: 'High' | 'Low';
  limitValue: number;
  valueAtRaise: number;
  unitSymbol: string | null;
  raisedAtUtc: string;
  state: AlarmState;
  acknowledgedAtUtc: string | null;
  /** Who acknowledged it, by their username at the time. */
  acknowledgedBy: string | null;
  clearedAtUtc: string | null;
  /** When a shelf ends; set only while shelved. */
  shelvedUntilUtc: string | null;
  /** First seen on the first evaluation after a Gateway restart, so likely began unwatched. */
  detectedAfterRestart: boolean;
}

/** A configured threshold on a tag. */
export interface AlarmDefinition {
  id: string;
  tagId: string;
  highLimit: number | null;
  lowLimit: number | null;
}

/**
 * A tag's history plus the names it was recorded under.
 *
 * The names resolve even when the tag or its device has been deleted, so a trend for
 * retired equipment reads as a name rather than an identifier.
 */
export interface TagHistory {
  tagId: string;
  tagName: string | null;
  deviceName: string | null;
  isDeleted: boolean;
  samples: HistorySample[];
}

/** A folder option in a picker, with its depth so the list can read as a tree. */
export interface FolderOption {
  id: string | null;
  label: string;
  depth: number;
}

/** Flattens a site's folders into a pickable list, parents before children. */
export function folderOptions(tree: SiteTree | null): FolderOption[] {
  const options: FolderOption[] = [{ id: null, label: 'Directly under the site', depth: 0 }];

  const walk = (folders: TreeFolder[], depth: number): void => {
    for (const folder of folders) {
      options.push({ id: folder.id, label: folder.name, depth });
      walk(folder.folders, depth + 1);
    }
  };

  walk(tree?.folders ?? [], 1);
  return options;
}

// ---- users and access (ADR-0011) --------------------------------------------

/** A role on one Site. Admin is not one of them: it is tenant-wide, a flag on the user. */
export type SiteRole = 'Viewer' | 'Operator';

export interface SiteRoleGrant {
  siteId: string;
  role: SiteRole;
}

/** A user and what they may do, as the gateway reports it. */
export interface Access {
  userId: string;
  username: string;
  isAdmin: boolean;
  sites: SiteRoleGrant[];
}

export interface LoginResponse {
  token: string;
  access: Access;
}

/**
 * One entry in the alarm journal (ADR-0013).
 *
 * `siteId` is null on the engine's own events — EvaluationStarted, EvaluationStopped and
 * JournalGap — which belong to no Site because an outage applies to the whole Gateway.
 * Every signed-in user sees those; alarm events are filtered to the reader's Sites by the
 * Gateway, never here.
 */
export interface AlarmEvent {
  type: string;
  recordedAtUtc: string;
  sourceTimeUtc: string | null;
  occurrenceId: string | null;
  definitionId: string | null;
  tagId: string | null;
  siteId: string | null;
  tagPath: string | null;
  limit: string | null;
  limitValue: number | null;
  value: number | null;
  unitSymbol: string | null;
  actorUsername: string | null;
  detectedAfterRestart: boolean;
  shelvedUntilUtc: string | null;
  reason: string | null;
  gapFromUtc: string | null;
  gapUntilUtc: string | null;
  unrecordedTransitions: number | null;
}

/**
 * What a numeric form field actually holds.
 *
 * An `<input type="number">` bound with `ngModel` hands over a **number** — or `null`
 * once the box is empty or its contents are not a number — never the string the draft
 * was seeded with. Declaring such a field `string` is a lie the compiler cannot catch,
 * and it is what let `raw.trim()` reach a number and throw on the first save of an alarm
 * threshold.
 */
export type NumberField = string | number | null | undefined;

/** Either a value — `null` meaning the field was left blank — or a refusal. */
export type ParsedNumber = { readonly ok: true; readonly value: number | null } | { readonly ok: false };

/**
 * Reads a number out of a form field, whatever the binding handed over.
 *
 * Blank stays blank: it becomes `null`, never `0`. For a limit those mean opposite
 * things — "no limit on this side" against "the limit is zero" — and zero is an
 * ordinary threshold. Anything that is not a number is refused rather than passed on as
 * `NaN`, which `JSON.stringify` would quietly turn into `null` and so into "no limit".
 */
export function parseNumberField(raw: NumberField): ParsedNumber {
  if (raw === null || raw === undefined) {
    return { ok: true, value: null };
  }

  if (typeof raw === 'number') {
    return Number.isFinite(raw) ? { ok: true, value: raw } : { ok: false };
  }

  const text = raw.trim();
  if (text.length === 0) {
    return { ok: true, value: null };
  }

  // A decimal comma is what a Macedonian keyboard produces. A thousands separator
  // would leave a second one behind and fail the check below, which is the right
  // answer: refuse it rather than guess which one the operator meant.
  const value = Number(text.replace(',', '.'));
  return Number.isFinite(value) ? { ok: true, value } : { ok: false };
}

/** Two digits, so 9:5:3 never reads as 9:5:3. */
function pad(value: number): string {
  return value.toString().padStart(2, '0');
}

/**
 * A measurement as an operator should read it.
 *
 * A double straight from the wire reads as `4.8100000000000005 bar`, which is the
 * arithmetic showing through rather than a pressure anyone measured. Two decimals, the
 * same as the live value elsewhere in the client, so the two never appear to disagree.
 */
export function formatMeasurement(value: number | null | undefined, unitSymbol?: string | null): string {
  if (value === null || value === undefined || !Number.isFinite(value)) {
    return '—';
  }

  const text = value.toFixed(2);
  return unitSymbol ? `${text} ${unitSymbol}` : text;
}

/**
 * The window a gap covers, as a reader can place in time.
 *
 * Times alone are enough while both ends fall on the same day. Across midnight they are
 * not: "15:31:33 – 14:13:44" reads as an interval that ran backwards, when it is in fact
 * an outage of nearly a day. Both ends then carry their date.
 */
export function formatGapWindow(
  from: string | Date | null | undefined,
  to: string | Date | null | undefined,
): string | null {
  const start = toDate(from);
  const end = toDate(to);

  if (start === null || end === null) {
    return null;
  }

  const sameDay =
    start.getFullYear() === end.getFullYear() &&
    start.getMonth() === end.getMonth() &&
    start.getDate() === end.getDate();

  const time = (at: Date) => `${pad(at.getHours())}:${pad(at.getMinutes())}:${pad(at.getSeconds())}`;
  const dated = (at: Date) =>
    `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())} ${time(at)}`;

  return sameDay ? `${time(start)} – ${time(end)}` : `${dated(start)} – ${dated(end)}`;
}

function toDate(value: string | Date | null | undefined): Date | null {
  if (value === null || value === undefined) {
    return null;
  }

  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

/**
 * Why an occurrence was retired, in words rather than in the engine's own vocabulary.
 *
 * An unknown reason is shown as it stands. A reason this client has not been taught is
 * still worth more to a reader than nothing at all, and silence would hide it from
 * whoever has to notice the gap.
 */
export function describeReason(reason: string | null | undefined): string | null {
  if (!reason) {
    return null;
  }

  switch (reason) {
    case 'superseded-by-new-breach':
      return 'replaced by a new alarm';
    case 'superseded':
      return 'replaced by a later occurrence';
    case 'definition-removed':
      return 'threshold deleted';
    case 'retirement-completed-at-startup':
      return 'closed at startup';
    default:
      return reason;
  }
}

/** How many devices a tree holds, at any depth of folders. */
export function deviceCount(tree: { folders: TreeFolder[]; devices: TreeDevice[] }): number {
  return tree.devices.length + tree.folders.reduce((sum, folder) => sum + deviceCount(folder), 0);
}

/**
 * The Site to open on: the first, in the order given, that has a device to look at, or the
 * first of all when none has. Found walking the Phase 6 gate: opening on a Site with nothing
 * in it — the seeded second Site holds only an empty folder — reads as "nothing works".
 *
 * @param treeOf Loads one Site's tree. Sites after the chosen one are never loaded.
 */
export async function siteToOpen(
  sites: Site[],
  treeOf: (siteId: string) => Promise<{ folders: TreeFolder[]; devices: TreeDevice[] }>,
): Promise<string | null> {
  for (const site of sites) {
    if (deviceCount(await treeOf(site.id)) > 0) {
      return site.id;
    }
  }

  return sites[0]?.id ?? null;
}

/** A Site's name for display, from the Sites the reader may see. */
export function siteName(sites: Site[], siteId: string): string {
  return sites.find((site) => site.id === siteId)?.name ?? '—';
}
