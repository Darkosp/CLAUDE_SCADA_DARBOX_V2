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
