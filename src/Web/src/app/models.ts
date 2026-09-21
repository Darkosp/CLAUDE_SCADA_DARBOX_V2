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
