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
  /**
   * The span this tag's readings are expected to fall in, or nulls when nothing is declared (ADR-0030).
   *
   * Carried on the tree as well as on a live reading because this is the shape the tag form reads
   * back: **a range an author cannot see is a range they cannot correct.**
   */
  rangeLow?: number | null;
  rangeHigh?: number | null;
}

export interface TreeDevice {
  id: string;
  name: string;
  driverKey: string;
  connectionSettings: Record<string, string>;
  /** Null for a pushing device, which has no scan interval (ADR-0016). */
  scanIntervalMs: number | null;
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
  /**
   * How long the condition must hold before the alarm is raised, or null for none (ADR-0025 §2).
   *
   * `null` and `0` are different and the difference is not cosmetic: zero is not a legal delay — the
   * server refuses it — and an alarm with no delay must not be shown as having one of zero.
   */
  onDelaySeconds: number | null;
  /** How far a value must come back past the limit before clearing, or null for none (ADR-0025 §3). */
  deadband: number | null;
}

/**
 * A tag's history plus the names it was recorded under.
 *
 * The names resolve even when the tag or its device has been deleted, so a trend for
 * retired equipment reads as a name rather than an identifier.
 *
 * **The payload is one of two answers, never a mixture** (ADR-0029): either every reading in the
 * window, in `samples`, or the window reduced to `buckets` with the width it was reduced to. Which
 * one it is, is read off `bucketMilliseconds`. This client always asks for a reduction; `samples` is
 * still here because the endpoint still answers that way when nothing asks it to reduce, and the
 * raw path is what a report will want.
 */
export interface TagHistory {
  tagId: string;
  tagName: string | null;
  deviceName: string | null;
  isDeleted: boolean;
  samples: HistorySample[];
  bucketMilliseconds: number | null;
  buckets: TrendBucket[];
}

/**
 * One bucket of a reduced trend, as the server sent it (ADR-0029 §4).
 *
 * A bucket is not a reading: it is what a stretch of the window held. The server is the only place
 * that decides which readings may be plotted, because it is the only place that can — the readings
 * themselves never leave it.
 */
export interface TrendBucket {
  /** The start of the stretch, on the grid this window's start set, so the first one is the edge. */
  startUtc: string;
  /**
   * The newest reading in it.
   *
   * **The only field freshness can be decided on, and `startUtc` is not a measurement time at all**:
   * it is the edge of the stretch, derived from the window, and no reading need have happened there.
   * An age measured from it would name a time nothing was measured at in *no reading since …*, and
   * would call a trend stale up to a bucket early — seventeen minutes early on a seven-day window
   * (ADR-0003, ADR-0029 §4).
   */
  lastUtc: string;
  /**
   * How many readings were read in it, whatever their quality — so a device answering with nothing
   * but Bad is a hole with a count rather than a quiet bucket. Never zero: a stretch nothing was
   * measured in is absent from the list entirely, which is how a gap is drawn.
   */
  count: number;
  /** The extremes of the readings a trend plots in this bucket, or null when it held none of those. */
  low: number | null;
  high: number | null;
}

/**
 * A trend's window as it is drawn: how wide one bucket is, and the buckets.
 *
 * Produced by `trendSeries` from what the server answered, so a chart cannot be handed a series
 * whose resolution it does not know.
 */
export interface TrendSeries {
  /**
   * How wide one bucket is, in milliseconds — the resolution this series was reduced to.
   *
   * Zero only on an empty series, where there is nothing to place and nothing divides by it.
   */
  bucketMilliseconds: number;
  buckets: TrendBucket[];
}

/**
 * A trend with nothing to draw: before it has been read, and when reading it failed.
 *
 * `bucketMilliseconds` is zero because no bucket is placed, so nothing divides by it — the chart's
 * own empty state is what this draws, and it draws it without asking how wide a bucket was.
 */
export const NO_TREND: TrendSeries = { bucketMilliseconds: 0, buckets: [] };

/**
 * The drawable part of a history response, or null when the server did not reduce it.
 *
 * **Null is a refusal, not a fallback.** This client asks for a reduction and cannot draw the
 * alternative: a raw answer is every reading in the window — 100,552 of them and 22 MB for seven
 * days of one tag, which is the measurement ADR-0029 exists for — so reducing it here would be the
 * code this change deletes, kept alive for a case that needs the Gateway and the client to be
 * different builds. The Gateway serves this client from its own origin as one build (Phase 6,
 * ADR-0028), so that is not a deployment this project supports.
 */
export function trendSeries(history: TagHistory): TrendSeries | null {
  const width = history.bucketMilliseconds;

  // A width of zero would divide a window into nothing; the server refuses to send one, and a
  // series that would draw at the same x for every bucket is worse than a series that draws nothing.
  if (width === null || width === undefined || !(width > 0)) {
    return null;
  }

  return { bucketMilliseconds: width, buckets: history.buckets ?? [] };
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

// ---- edges (ADR-0019) -------------------------------------------------------

/**
 * An edge: one agent on the plant floor that reads the devices assigned to it and pushes their
 * values on. It hangs off the deployment rather than a Site, and its name is the identity in
 * its own certificate (ADR-0019).
 */
export interface Edge {
  id: string;
  name: string;
  /** The device carrying its link, or null while it has none. */
  linkDeviceId: string | null;
  /** The devices it reads. Assigning one is an ordinary edit of the device (ADR-0019). */
  deviceIds: string[];
  /**
   * The driver keys the edge itself says its build has, or null when it has never said
   * (ADR-0019 §8).
   *
   * null and [] are different answers and are shown differently: null is "nobody has told us",
   * [] is "this edge says it has none". Neither is ever filled in from the Gateway's own
   * drivers, which are a different list.
   */
  declaredDriverKeys: string[] | null;
  /** When the cloud read that declaration, or null when it never has. */
  driversDeclaredAtUtc: string | null;
  /**
   * The devices assigned to this edge that are not being read, each with the driver it needs
   * (ADR-0021). Empty means every assigned device is being read.
   *
   * Two facts arrive here as one list, because an operator has one question — "is this device
   * being read?" — and it has two causes: a device assigned to an edge that declared it lacks the
   * driver (ADR-0019 §8), and a device the edge itself reported it cannot open (ADR-0021), which
   * is the case no save re-examines. `reportedByEdge` says which, because the second is stronger:
   * the edge tried.
   */
  unreadableDevices: UnreadableDevice[] | null;
  /**
   * How long this edge's link may be silent before the tags of the devices it reads go Bad, in
   * seconds (ADR-0016, ADR-0022).
   *
   * The edge's own setting rather than the link device's, because the edge *is* the link: the
   * Gateway derives the device and it is not overridable, so this is the only place the limit is
   * chosen. It is the one setting with a reason to vary per plant.
   */
  linkStalenessSeconds: number;
  /** How long the broker queues for this edge while the Gateway is away (ADR-0022). */
  linkSessionExpiryHours: number;
}

/** A device an edge is assigned and is not reading, and the driver it needs (ADR-0021). */
export interface UnreadableDevice {
  /** The cloud's own device, or null when the name the edge reported no longer resolves. */
  deviceId: string | null;
  device: string;
  driver: string;
  reportedByEdge: boolean;
}

/**
 * What an edge has said about its own drivers, in a sentence, or null when there is nothing to
 * say yet because the edge has not declared.
 *
 * The driver keys are the edge's own statement, so there is no form field for them: this reads
 * back what the edge said rather than letting anyone type it.
 */
export function declarationOf(edge: Edge): string {
  if (!edge.declaredDriverKeys) {
    return (
      'This edge has not declared its drivers yet. Until it does, a device may be assigned to ' +
      'it whatever driver it names — the cloud cannot check, and says so rather than guessing.'
    );
  }

  return edge.declaredDriverKeys.length === 0
    ? 'This edge has declared it has no drivers at all, so no device assigned to it can be read.'
    : `This edge has declared it has: ${edge.declaredDriverKeys.map((key) => `'${key}'`).join(', ')}.`;
}

/**
 * What an edge cannot read, in a sentence, or null when there is nothing wrong (ADR-0021).
 *
 * This is the direction ADR-0019 §8 cannot see: a device assigned before the edge's build lost a
 * driver is never re-examined by any save, so the cloud would otherwise hold a device it believes
 * is being read while nothing reads it. The edge reports it, the cloud records it, and this is
 * where an operator reads it — the assignment is deliberately not changed, so nothing else would
 * tell them.
 */
export function unreadableOf(edge: Edge): string | null {
  const devices = edge.unreadableDevices ?? [];

  if (devices.length === 0) {
    return null;
  }

  const named = devices.map((device) => `'${device.device}' (needs '${device.driver}')`).join(', ');

  return devices.length === 1
    ? `This edge is assigned ${named} and cannot read it. Its tags will read Bad until the device is unassigned or the edge's build has that driver.`
    : `This edge is assigned ${devices.length} devices it cannot read: ${named}. Their tags will read Bad until they are unassigned or the edge's build has those drivers.`;
}

/**
 * Why the edge chosen on a device form cannot read a device with this driver, or null.
 *
 * The Gateway refuses the assignment, so this is the same answer earlier — and an edge that has
 * declared nothing is deliberately not refused: it may simply never have started, and a plant's
 * devices are configured before its edge is (ADR-0019 §8).
 */
export function edgeDriverNote(edge: Edge | null, driverKey: string): string | null {
  if (edge === null || !edge.declaredDriverKeys) {
    return null;
  }

  return edge.declaredDriverKeys.some((key) => key.toLowerCase() === driverKey.toLowerCase())
    ? null
    : `Edge "${edge.name}" has declared it cannot read '${driverKey}'; the drivers it has declared are ${
        edge.declaredDriverKeys.length === 0
          ? 'none at all'
          : edge.declaredDriverKeys.map((key) => `'${key}'`).join(', ')
      }.`;
}

/** An edge as a picker option, where null is "not on an edge". */
export interface EdgeOption {
  id: string | null;
  label: string;
}

/** The edges as a picker, "not on an edge" first. */
export function edgeOptions(edges: Edge[]): EdgeOption[] {
  return [
    { id: null, label: 'Not on an edge' },
    ...edges.map((edge) => ({ id: edge.id, label: edge.name })),
  ];
}

/**
 * The edge a device is assigned to, or null.
 *
 * Read from the edges rather than from the tree, because the tree carries what a device is and
 * the assignment is not one of those things: it belongs to the edge, which is why assigning is
 * an ordinary edit of the device and nothing else (ADR-0019).
 */
export function edgeOfDevice(edges: Edge[], deviceId: string): Edge | null {
  return edges.find((edge) => edge.deviceIds.includes(deviceId)) ?? null;
}

/** Every device in a site tree, folders walked. */
export function treeDevices(
  tree: { folders: TreeFolder[]; devices: TreeDevice[] } | null,
): TreeDevice[] {
  const all: TreeDevice[] = [...(tree?.devices ?? [])];

  const walk = (folders: TreeFolder[]): void => {
    for (const folder of folders) {
      all.push(...folder.devices);
      walk(folder.folders);
    }
  };

  walk(tree?.folders ?? []);
  return all;
}

/**
 * What an edge reads, as far as the tree being browsed knows it.
 *
 * The client holds one Site's tree at a time and an edge is tenant-wide, so a device this tree
 * does not have is counted rather than named: a name would have to be invented for it.
 */
export function edgeReads(
  tree: { folders: TreeFolder[]; devices: TreeDevice[] } | null,
  edge: Edge,
): { names: string[]; elsewhere: number } {
  const devices = treeDevices(tree);
  const names: string[] = [];
  let elsewhere = 0;

  for (const id of edge.deviceIds) {
    const device = devices.find((candidate) => candidate.id === id);
    if (device === undefined) {
      elsewhere += 1;
    } else {
      names.push(device.name);
    }
  }

  return { names, elsewhere };
}

/**
 * The devices an edge's link may be: the pushing devices of the Site being browsed, and the one
 * it already has even when that device is not in this Site.
 *
 * A link has to be a pushing device, because a polled one would leave every device the edge
 * reads with nothing reading it (ADR-0016). The one it already has is kept in the list even
 * when this Site cannot offer it, because a picker that dropped it would release the link the
 * next time anything on the form was saved.
 */
export function linkOptions(
  devices: TreeDevice[],
  drivers: DriverShape[],
  current: string | null,
): EdgeOption[] {
  const pushing = devices.filter((device) => pushes(drivers, device.driverKey));
  const options: EdgeOption[] = [
    { id: null, label: 'No link' },
    ...pushing.map((device) => ({ id: device.id, label: device.name })),
  ];

  if (current !== null && !pushing.some((device) => device.id === current)) {
    options.push({ id: current, label: 'A device outside this Site' });
  }

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
 *
 * SamplesLost and SourceClockSkew are about a pushing source — an edge (ADR-0017). They name
 * a device and its Site, and no alarm.
 */
/**
 * What a reader has narrowed the journal to, before it is sent.
 *
 * Every field is optional and an absent one means "no opinion" rather than "none of these", which is
 * why the empty list is left out of the request entirely: `tag=` with nothing after it would be a
 * filter matching nothing, and the reader would see an empty journal with no way to tell that from a
 * quiet night.
 */
export interface JournalFilters {
  readonly tagIds?: readonly string[];
  readonly types?: readonly string[];
  readonly from?: string | null;
  readonly to?: string | null;
  readonly limit?: number;
}

/**
 * The event types a reader may narrow to, in the order they appear in the picker.
 *
 * The engine's own three are included on purpose rather than hidden. An operator looking for one
 * alarm's history is still owed the news that nothing was being watched for part of the window, and
 * a filter list that omitted them would make that row unreachable — the exact wrong answer ADR-0013
 * exists to prevent.
 */
export const ALARM_EVENT_TYPES = [
  'Raised',
  'Acknowledged',
  'Shelved',
  'Unshelved',
  'Cleared',
  'Retired',
  'EvaluationStarted',
  'EvaluationStopped',
  'JournalGap',
  'SamplesLost',
  'SourceClockSkew',
] as const;

/**
 * The query string a set of journal filters becomes (Phase 5.5's deferred filtering).
 *
 * Pure, and here rather than in `api.ts`, because `api.ts` injects Angular and cannot be loaded by
 * the Node test runner — and this is the part of a filter worth testing. **Two of its rules are the
 * difference between a working filter and a silently broken one:**
 *
 * - an absent filter is left out entirely rather than sent empty. `tag=` with nothing after it is a
 *   filter matching nothing, so a reader who had picked no tag would see an empty journal and
 *   conclude the plant had been quiet.
 * - a filter with several values is repeated rather than joined, because the server reads it as a
 *   list and a comma-joined value is one unknown tag id.
 */
export function journalQuery(filters: JournalFilters = {}): string {
  const query = new URLSearchParams();
  query.set('limit', String(filters.limit ?? 200));

  for (const tagId of filters.tagIds ?? []) {
    query.append('tag', tagId);
  }

  for (const type of filters.types ?? []) {
    query.append('type', type);
  }

  if (filters.from) {
    query.set('from', filters.from);
  }

  if (filters.to) {
    query.set('to', filters.to);
  }

  return query.toString();
}

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
  deviceId: string | null;
  lostSamples: number | null;
  clockSkewSeconds: number | null;
}

/**
 * Whether an entry is the engine speaking: it names neither an alarm nor a Site. A source's
 * own event names no alarm either, but it does name the Site its device is on.
 */
export function isEngineEvent(entry: Pick<AlarmEvent, 'occurrenceId' | 'siteId'>): boolean {
  return entry.occurrenceId === null && entry.siteId === null;
}

/**
 * What a source's own journal entry says, in words: how many samples it dropped, or how far
 * its clock is from the Gateway's and in which direction. Null for every other entry.
 */
export function describeSourceEvent(
  entry: Pick<AlarmEvent, 'type' | 'lostSamples' | 'clockSkewSeconds'>,
): string | null {
  if (entry.type === 'SamplesLost' && entry.lostSamples !== null) {
    return `${entry.lostSamples} ${entry.lostSamples === 1 ? 'sample' : 'samples'} dropped at the source`;
  }

  if (entry.type === 'SourceClockSkew' && entry.clockSkewSeconds !== null) {
    const direction = entry.clockSkewSeconds > 0 ? 'ahead of' : 'behind';
    return `source clock ${formatDuration(Math.abs(entry.clockSkewSeconds))} ${direction} the Gateway's`;
  }

  return null;
}

/** A length of time as a reader would say it: seconds, then minutes, then hours. */
function formatDuration(seconds: number): string {
  if (seconds < 120) {
    return `${Math.round(seconds)} s`;
  }

  if (seconds < 7200) {
    return `${Math.round(seconds / 60)} min`;
  }

  return `${(seconds / 3600).toFixed(1)} h`;
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

// ---- trend windows ----------------------------------------------------------
//
// **A curve with no stated period is a curve nobody can reason about.** Until 2026-10-07 the client
// asked for fifteen minutes, hard-coded in two places, and the chart said only how many samples it
// held and what their highest and lowest values were. Nothing on the screen said whether the line
// covered a quarter of an hour or a week, and an operator looking into something that happened two
// hours ago had no way to ask for it.

/** How long a trend looks back, as an operator chooses it. */
export interface TrendWindow {
  /** What the picker says. */
  label: string;
  /** How far back from now, in milliseconds. */
  milliseconds: number;
}

/**
 * The windows offered, shortest first.
 *
 * A fixed list rather than two date fields, for the reason ADR-0025's shelving expiry is a fixed
 * list: the question an operator is really asking is *how far back*, and a pair of date pickers makes
 * them answer a harder question than they have. Fifteen minutes stays first because it is what every
 * trend showed before this existed, so nothing an author built changes appearance by default.
 */
export const TREND_WINDOWS: readonly TrendWindow[] = [
  { label: '15 minutes', milliseconds: 15 * 60 * 1000 },
  { label: '1 hour', milliseconds: 60 * 60 * 1000 },
  { label: '8 hours', milliseconds: 8 * 60 * 60 * 1000 },
  { label: '24 hours', milliseconds: 24 * 60 * 60 * 1000 },
  { label: '7 days', milliseconds: 7 * 24 * 60 * 60 * 1000 },
];

/**
 * How many points a trend **asks the server for**, and the width it is drawn at before it has been
 * measured (ADR-0029 §3).
 *
 * **A budget, not the pixel width.** The chart fills whatever card it is in and measures itself
 * (see `TrendChart.width`), so on a wide pane the same 600 points are drawn across more pixels —
 * a smooth line rather than a loss, with the resolution stated under the chart ("593 points"). Asking
 * for exactly the measured width is the next step and it is not a one-liner: a screen may hold two
 * trends on one tag in tiles of different widths, one fetch serves both, and the budget would then be
 * the widest of them. Recorded in `open-work.md` §2.6.
 *
 * It is what bounds the answer, and that is the point: without it the server answers with every
 * reading in the window, which for seven days of a tag scanned once a second is 100,552 readings and
 * 22 MB.
 */
export const TREND_POINTS = 600;

/** The window a label names, or the first one when the label is not among them. */
export function trendWindowOf(label: string): TrendWindow {
  return TREND_WINDOWS.find((window) => window.label === label) ?? TREND_WINDOWS[0];
}

/**
 * Where a reading sits across the chart, as a fraction of **the window that was asked for**.
 *
 * Not of the span between the first and last sample it happens to hold, which is what this used to
 * be. The difference is the whole point: a tag whose device was offline for the first ten minutes of
 * a quarter-hour window should show **ten minutes of empty chart** and then a line — not a line
 * stretched across the full width as though it had been there all along. That is the same mistake,
 * in the same component, that `f22b9e4` fixed for a gap in the middle (ADR-0003), left standing at
 * the ends.
 *
 * Clamped, because a sample fractionally outside the window — the server's bounds are inclusive and
 * clocks are not identical — belongs at the edge rather than off the chart.
 */
export function plotAcross(time: number, from: number, to: number, width: number): number {
  const span = Math.max(to - from, 1);
  return Math.max(0, Math.min(1, (time - from) / span)) * width;
}

/**
 * One column of a trend: what to draw at one x, and which bucket of the window it came from.
 */
export interface TrendColumn {
  /** Where across the chart, in the chart's own units. */
  x: number;
  /** The lowest and highest values in this bucket — the envelope, not an average. */
  low: number;
  high: number;
  /**
   * Which bucket of the window's grid this is, counted from the window's start.
   *
   * Kept so a **missing** bucket can be seen: the buckets the server sends are the stretches it
   * measured something in, so the ordinal skipping a number is exactly one gap (ADR-0029 §6).
   */
  ordinal: number;
}

/**
 * A reduced trend, as the columns a chart draws.
 *
 * **The reduction itself is not here any more.** It was, in `trendColumns`, from 2026-10-07 until
 * ADR-0029: min and max per pixel, computed in the browser over every reading the server had sent.
 * It moved into the query, because the readings were crossing the wire to be thrown away — and the
 * rule that mattered did not change with the move: **the extremes of what was measured, never an
 * average and never every n-th reading**, because the one-second excursion that tripped an alarm is
 * exactly the sample a thinning pass discards.
 *
 * A bucket the server read nothing plottable in is skipped: it is a hole in the line with a count
 * beside it, not a value.
 */
export function bucketColumns(
  series: TrendSeries,
  from: number,
  to: number,
  width: number,
): TrendColumn[] {
  const columns: TrendColumn[] = [];

  for (const bucket of series.buckets) {
    if (bucket.low === null || bucket.high === null) {
      continue;
    }

    const start = new Date(bucket.startUtc).getTime();

    if (Number.isNaN(start)) {
      continue;
    }

    // **The midpoint of the bucket's span.** The readings inside it are within one bucket width of
    // that position by definition, which is the honest place to draw them once their own times are
    // no longer in the payload — and it is what makes the ordinals below line up with the window.
    columns.push({
      x: plotAcross(start + series.bucketMilliseconds / 2, from, to, width),
      low: bucket.low,
      high: bucket.high,
      ordinal: Math.round((start - from) / series.bucketMilliseconds),
    });
  }

  // Sorted here rather than trusted: the API promises no order, and a chart that drew in a wrong
  // one would fold the line back on itself.
  return columns.sort((left, right) => left.ordinal - right.ordinal);
}

/**
 * The indexes after which the line must break, because a bucket between two columns is missing.
 *
 * **This is exact, and the rule it replaced could not be.** Until ADR-0029 the chart inferred an
 * outage by comparing the space between columns against four times their median, which cannot see a
 * hole narrower than about four columns — and at a resolution of 1008 s, four columns is over an
 * hour of nothing. A bucket the server sent is a stretch it measured in, so a skipped ordinal is a
 * stretch it did not, at every resolution the window can be drawn at.
 */
export function bucketGaps(columns: readonly TrendColumn[]): Set<number> {
  const gaps = new Set<number>();

  for (let index = 1; index < columns.length; index += 1) {
    if (columns[index].ordinal - columns[index - 1].ordinal > 1) {
      gaps.add(index - 1);
    }
  }

  return gaps;
}

/**
 * How many readings the plotted series was reduced from — the sum of the buckets' counts.
 *
 * Exact, because every reading in the window falls in exactly one bucket and every non-empty bucket
 * is sent. It is what the caption has always shown, and the reason the caption can still say how
 * much is behind a curve that is now drawn from far fewer points than it holds.
 */
export function readingsIn(series: TrendSeries): number {
  return series.buckets.reduce((total, bucket) => total + bucket.count, 0);
}

/**
 * The two ends of a trend's time axis, as a reader can place them.
 *
 * Times alone within a day, dates when it crosses one — the rule `formatGapWindow` arrived at by
 * being wrong first: "15:31 – 14:13" reads as an interval running backwards when it is in fact
 * nearly a day. A seven-day window crosses midnight every time, so this is not an edge case here.
 */
export function trendAxis(from: Date, to: Date): { start: string; end: string } {
  const sameDay =
    from.getFullYear() === to.getFullYear() &&
    from.getMonth() === to.getMonth() &&
    from.getDate() === to.getDate();

  const time = (at: Date) => `${pad(at.getHours())}:${pad(at.getMinutes())}`;
  const dated = (at: Date) => `${pad(at.getDate())}/${pad(at.getMonth() + 1)} ${time(at)}`;

  return sameDay
    ? { start: time(from), end: time(to) }
    : { start: dated(from), end: dated(to) };
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

/**
 * A tag's path without its leading Site, for a table that names the Site in its own column.
 * Only a leading segment that is exactly that Site's name is removed; anything else is shown
 * whole rather than guessed at.
 */
export function pathWithinSite(tagPath: string, siteName: string): string {
  const prefix = `${siteName}/`;
  return tagPath.startsWith(prefix) ? tagPath.slice(prefix.length) : tagPath;
}

/**
 * The forms whose name the Gateway may refuse as already taken (ADR-0015). An edge's name is
 * unique in the deployment rather than in a Site, and it is refused the same way.
 */
export type NameField = 'folder' | 'device' | 'tag' | 'instance' | 'templateTag' | 'edge';

/**
 * The Gateway's reason when it refused a name as already taken — a 409 — or null for any other
 * failure, which belongs in the general error line rather than against the name field.
 */
export function nameConflictMessage(error: unknown): string | null {
  const refusal = error as { status?: unknown; message?: unknown } | null;
  return refusal !== null && typeof refusal === 'object' && refusal.status === 409 && typeof refusal.message === 'string'
    ? refusal.message
    : null;
}

/**
 * Runs one save at a time: a second press while the first is still in flight does nothing.
 *
 * A courtesy against a double click, and no more (ADR-0015). It cannot help a retry after a
 * lost reply, or two people saving the same name; the database's unique index is what
 * guarantees that, and a 409 is how it says so.
 */
export class SingleFlight {
  private running = false;

  constructor(private readonly onBusyChange: (busy: boolean) => void = () => undefined) {}

  get busy(): boolean {
    return this.running;
  }

  /** Runs the action unless one is already running; says whether it ran. */
  async run(action: () => Promise<void>): Promise<boolean> {
    if (this.running) {
      return false;
    }

    this.running = true;
    this.onBusyChange(true);
    try {
      await action();
      return true;
    } finally {
      this.running = false;
      this.onBusyChange(false);
    }
  }
}

/** A driver this build has, and whether it pushes its values rather than being polled (ADR-0016). */
export interface DriverShape {
  key: string;
  pushing: boolean;
}

/** Whether devices of this driver push; an unknown key is treated as polled, as the Gateway does. */
export function pushes(drivers: DriverShape[], driverKey: string): boolean {
  const key = driverKey.trim().toLowerCase();
  return drivers.some((driver) => driver.pushing && driver.key.toLowerCase() === key);
}

/**
 * The scan interval to send for a device: none for a pushing driver, which has no scan interval
 * and would be refused one (ADR-0016); a positive number of milliseconds for a polled one.
 */
export function scanIntervalToSend(
  drivers: DriverShape[],
  driverKey: string,
  raw: NumberField,
): { ok: true; value: number | null } | { ok: false; error: string } {
  if (pushes(drivers, driverKey)) {
    return { ok: true, value: null };
  }

  const scan = parseNumberField(raw);
  if (!scan.ok || scan.value === null || scan.value <= 0) {
    return { ok: false, error: 'Scan interval must be a positive number of milliseconds.' };
  }

  return { ok: true, value: scan.value };
}

/**
 * What to say about a tag that has never received anything (ADR-0016): "no data since" the moment
 * the Gateway began listening — an observed time, not a measured one — so a device that was never
 * set up reads differently from one that fell silent. Null for any other tag.
 */
export function noDataNote(
  snapshot: { noDataSinceUtc?: string | null },
  locale?: string,
): string | null {
  if (!snapshot.noDataSinceUtc) {
    return null;
  }

  const since = new Date(snapshot.noDataSinceUtc).toLocaleString(locale, {
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });

  return `No data since ${since}`;
}

// ---- the audit trail (ADR-0032) ---------------------------------------------------------------

export interface AuditFilters {
  /** An action *prefix*: `auth.` asks for the family, `auth.login` for one action in it. */
  readonly action?: string | null;
  readonly actor?: string | null;
  readonly from?: string | null;
  readonly to?: string | null;
  /** The cursor: return rows older than this id (ADR-0032 §2). */
  readonly before?: number | null;
  readonly limit?: number;
}

export interface AuditEntry {
  id: number;
  occurredAtUtc: string;
  actorUserId: string | null;
  /** The actor's name, resolved by the server when the row is read — so an ex-account still reads as a person. */
  actor: string | null;
  action: string;
  entityType: string | null;
  entityId: string | null;
  /** The writer's opaque document, as text. Never parsed here (ADR-0032 §6). */
  detail: string;
}

export interface AuditPage {
  entries: AuditEntry[];
  total: number;
}

/**
 * The query string a set of trail filters becomes.
 *
 * Pure, and here rather than in `api.ts`, for the same reason `journalQuery` is: `api.ts` injects
 * Angular and cannot be loaded by the Node test runner, and this is the part worth testing. **The rule
 * that matters is the absent filter**: `action=` with nothing after it is a filter matching nothing, so
 * an Admin who had typed nothing would be shown an empty trail and would read it as a system that had
 * done nothing — which is the one thing a trail must never look like.
 */
export function auditQuery(filters: AuditFilters = {}): string {
  const query = new URLSearchParams();
  query.set('limit', String(filters.limit ?? 200));

  const action = filters.action?.trim();
  if (action) {
    query.set('action', action);
  }

  if (filters.actor) {
    query.set('actor', filters.actor);
  }

  if (filters.from) {
    query.set('from', filters.from);
  }

  if (filters.to) {
    query.set('to', filters.to);
  }

  // `before` is a cursor and not a filter: id 0 is not a row, and 0 is falsy, so it is tested for
  // presence rather than truth. A cursor sent as `before=0` would ask for nothing at all.
  if (filters.before !== undefined && filters.before !== null) {
    query.set('before', String(filters.before));
  }

  return query.toString();
}

/**
 * What the reader is looking at, said plainly: *the newest 200 of 4,312* (ADR-0032 §3).
 *
 * A capped page that does not say it is capped is the journal's own lesson — a limit nobody mentions
 * looks like a quiet night — and this is the sentence that stops it.
 */
export function auditPageNote(page: AuditPage): string {
  const shown = page.entries.length;

  if (page.total === 0) {
    return 'Nothing recorded matches.';
  }

  if (shown === 0) {
    return `Nothing older than the last row shown; ${page.total} in the trail for these filters.`;
  }

  return shown >= page.total
    ? `${page.total} ${page.total === 1 ? 'entry' : 'entries'}, all of them.`
    : `The newest ${shown} of ${page.total} entries — see the older ones below.`;
}

