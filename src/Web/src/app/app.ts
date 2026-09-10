import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from './api';
import { BrowseTree, Selection } from './browse-tree';
import { TrendChart } from './trend-chart';
import { FolderOption, HistorySample, Site, SiteTree, TreeDevice, folderOptions } from './models';
import { TagStream } from './tag-stream';
import { formatValue, TagSnapshot } from './tag';
import { UNIT_PRESETS, unitBySymbol } from './units';

/** Editable shape of a device, kept separate from the wire form. */
interface DeviceDraft {
  id: string | null;
  name: string;
  driverKey: string;
  host: string;
  port: string;
  unitId: string;
  scanIntervalMs: number;
  folderId: string | null;
}

/** A delete the operator has started but not yet confirmed. */
interface PendingDelete {
  kind: 'folder' | 'device' | 'tag';
  id: string;
  ownerId: string;
  label: string;
  warning: string;
}

interface TagDraft {
  name: string;
  valueKind: 'Numeric' | 'Boolean';
  unitSymbol: string;
  sourceAddress: string;
}

@Component({
  selector: 'app-root',
  imports: [BrowseTree, TrendChart, FormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App implements OnInit {
  private readonly api = inject(Api);
  private readonly stream = inject(TagStream);

  protected readonly connection = this.stream.state;

  protected readonly sites = signal<Site[]>([]);
  protected readonly siteId = signal<string | null>(null);
  protected readonly tree = signal<SiteTree | null>(null);
  protected readonly selection = signal<Selection | null>(null);
  protected readonly history = signal<HistorySample[]>([]);
  protected readonly error = signal<string | null>(null);

  /**
   * What a Delete button is currently asking the operator to confirm.
   *
   * Deletion is confirmed inline rather than through window.confirm: the dialog gives no
   * room to name what is about to happen, and for a folder the rule — contents are never
   * removed with it — is exactly what needs saying at that moment.
   */
  protected readonly pendingDelete = signal<PendingDelete | null>(null);

  protected readonly deviceDraft = signal<DeviceDraft | null>(null);
  protected readonly tagDraft = signal<TagDraft | null>(null);
  protected readonly newFolderName = signal('');

  protected readonly unitPresets = UNIT_PRESETS;

  protected readonly folderChoices = computed<FolderOption[]>(() => folderOptions(this.tree()));

  /** The live value of the selected tag, or null before its first reading. */
  protected readonly liveValue = computed<TagSnapshot | null>(() => {
    const tagId = this.selection()?.tag?.id;
    return tagId ? (this.stream.tags().find((snapshot) => snapshot.tagId === tagId) ?? null) : null;
  });

  protected readonly selectedIsNumeric = computed(() => this.selection()?.tag?.valueKind === 'Numeric');

  async ngOnInit(): Promise<void> {
    void this.stream.start();
    await this.loadSites();
  }

  protected format(snapshot: TagSnapshot): string {
    return formatValue(snapshot);
  }

  protected async loadSites(): Promise<void> {
    try {
      const sites = await this.api.sites();
      this.sites.set(sites);

      if (sites.length > 0 && this.siteId() === null) {
        await this.selectSite(sites[0].id);
      }
    } catch (error) {
      this.report(error);
    }
  }

  protected async selectSite(siteId: string): Promise<void> {
    this.siteId.set(siteId);
    this.selection.set(null);
    this.history.set([]);
    await this.reloadTree();
  }

  protected async reloadTree(): Promise<void> {
    const siteId = this.siteId();
    if (!siteId) {
      return;
    }

    try {
      this.tree.set(await this.api.tree(siteId));
    } catch (error) {
      this.report(error);
    }
  }

  protected async select(selection: Selection): Promise<void> {
    this.selection.set(selection);
    this.pendingDelete.set(null);
    this.tagDraft.set(null);
    this.deviceDraft.set(null);
    this.history.set([]);

    if (selection.tag?.valueKind === 'Numeric') {
      await this.loadHistory();
    }
  }

  protected async loadHistory(): Promise<void> {
    const tag = this.selection()?.tag;
    if (!tag) {
      return;
    }

    try {
      const to = new Date();
      const from = new Date(to.getTime() - 15 * 60 * 1000);
      this.history.set((await this.api.history(tag.id, from, to)).samples);
    } catch (error) {
      this.report(error);
    }
  }

  // ---- folders ------------------------------------------------------------

  protected async addFolder(): Promise<void> {
    const siteId = this.siteId();
    const name = this.newFolderName().trim();

    if (!siteId || name.length === 0) {
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.createFolder(siteId, { name, parentFolderId: null });
      this.newFolderName.set('');
      await this.reloadTree();
    });
  }

  // ---- devices ------------------------------------------------------------

  protected startNewDevice(): void {
    this.selection.set(null);
    this.deviceDraft.set({
      id: null,
      name: '',
      driverKey: 'modbus-tcp',
      host: '127.0.0.1',
      port: '5502',
      unitId: '1',
      scanIntervalMs: 1000,
      folderId: null,
    });
  }

  protected editDevice(device: TreeDevice): void {
    this.deviceDraft.set({
      id: device.id,
      name: device.name,
      driverKey: device.driverKey,
      host: device.connectionSettings['host'] ?? '',
      port: device.connectionSettings['port'] ?? '',
      unitId: device.connectionSettings['unitId'] ?? '1',
      scanIntervalMs: device.scanIntervalMs,
      folderId: device.folderId,
    });
  }

  protected async saveDevice(): Promise<void> {
    const siteId = this.siteId();
    const draft = this.deviceDraft();

    if (!siteId || !draft) {
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.saveDevice(siteId, draft.id, {
        name: draft.name,
        driverKey: draft.driverKey,
        connectionSettings: { host: draft.host, port: draft.port, unitId: draft.unitId },
        scanIntervalMs: draft.scanIntervalMs,
        folderId: draft.folderId,
      });

      this.deviceDraft.set(null);
      await this.reloadTree();
    });
  }

  // ---- tags ---------------------------------------------------------------

  protected startNewTag(): void {
    this.tagDraft.set({
      name: '',
      valueKind: 'Numeric',
      unitSymbol: 'bar',
      sourceAddress: 'holding:0?scale=0.01',
    });
  }

  protected async saveTag(): Promise<void> {
    const device = this.selection()?.device;
    const draft = this.tagDraft();

    if (!device || !draft) {
      return;
    }

    await this.withErrorHandling(async () => {
      // A unit belongs only on a numeric tag; the gateway refuses it elsewhere, and
      // sending one anyway would just produce an error the operator cannot act on.
      const unit = draft.valueKind === 'Numeric' ? unitBySymbol(draft.unitSymbol) : null;

      await this.api.saveTag(device.id, null, {
        name: draft.name,
        valueKind: draft.valueKind,
        unit,
        sourceAddress: draft.sourceAddress,
        isWritable: false,
      });

      this.tagDraft.set(null);
      await this.reloadTree();
    });
  }

  // ---- deletion -----------------------------------------------------------

  protected askDeleteFolder(folderId: string, name: string): void {
    this.pendingDelete.set({
      kind: 'folder',
      id: folderId,
      ownerId: this.siteId() ?? '',
      label: `folder "${name}"`,
      warning: 'Anything inside it must be moved or deleted first — deleting a folder never removes its contents.',
    });
  }

  protected askDeleteDevice(device: TreeDevice): void {
    this.pendingDelete.set({
      kind: 'device',
      id: device.id,
      ownerId: this.siteId() ?? '',
      label: `device "${device.name}"`,
      warning: 'Its tags are deleted with it. Recorded history is kept and still shows their names.',
    });
  }

  protected askDeleteTag(deviceId: string, tagId: string, name: string): void {
    this.pendingDelete.set({
      kind: 'tag',
      id: tagId,
      ownerId: deviceId,
      label: `tag "${name}"`,
      warning: 'Recorded history is kept and still shows this name.',
    });
  }

  protected async confirmDelete(): Promise<void> {
    const pending = this.pendingDelete();
    if (!pending) {
      return;
    }

    await this.withErrorHandling(async () => {
      if (pending.kind === 'folder') {
        await this.api.deleteFolder(pending.ownerId, pending.id);
      } else if (pending.kind === 'device') {
        await this.api.deleteDevice(pending.ownerId, pending.id);
      } else {
        await this.api.deleteTag(pending.ownerId, pending.id);
      }

      this.pendingDelete.set(null);
      this.selection.set(null);
      this.history.set([]);
      await this.reloadTree();
    });
  }

  private async withErrorHandling(action: () => Promise<void>): Promise<void> {
    this.error.set(null);
    try {
      await action();
    } catch (error) {
      this.report(error);
    }
  }

  private report(error: unknown): void {
    this.error.set(error instanceof ApiError ? error.message : String(error));
  }
}
