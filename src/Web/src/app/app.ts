import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from './api';
import { BrowseTree, Selection } from './browse-tree';
import { TrendChart } from './trend-chart';
import {
  Alarm,
  AlarmDefinition,
  DeviceTemplate,
  FolderOption,
  HistorySample,
  SettingEntry,
  Site,
  SiteTree,
  TemplateTag,
  TreeDevice,
  folderOptions,
  mapToSettings,
  settingsToMap,
} from './models';
import { TagStream } from './tag-stream';
import { formatValue, TagSnapshot } from './tag';
import { UNIT_PRESETS, unitBySymbol } from './units';

/** Editable shape of a device, kept separate from the wire form. */
interface DeviceDraft {
  id: string | null;
  name: string;
  driverKey: string;
  /**
   * Named settings rather than fixed fields. Modbus wants host/port/unitId and OPC UA
   * wants endpointUrl; core treats both as opaque (ADR-0002), and so does this form.
   */
  settings: SettingEntry[];
  scanIntervalMs: number;
  folderId: string | null;
}

/** A device being created from a template, with the parameters that template asks for. */
interface InstantiateDraft {
  templateId: string;
  name: string;
  driverKey: string;
  settings: SettingEntry[];
  scanIntervalMs: number;
  folderId: string | null;
  parameters: SettingEntry[];
}

interface TemplateTagDraft {
  name: string;
  valueKind: 'Numeric' | 'Boolean';
  unitSymbol: string;
  addressTemplate: string;
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
  imports: [BrowseTree, TrendChart, FormsModule, DatePipe],
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

  protected readonly alarms = this.stream.alarms;

  /** Alarms worth interrupting for: raised, not yet seen, not shelved. */
  protected readonly unacknowledged = computed(() =>
    this.alarms().filter((alarm) => alarm.state === 'Active' || alarm.state === 'Cleared'),
  );

  /** The threshold configured on the selected tag, if any. */
  /** Which half of the app is on screen: browsing, or managing templates. */
  protected readonly view = signal<'browse' | 'templates'>('browse');

  protected readonly templates = signal<DeviceTemplate[]>([]);
  protected readonly selectedTemplate = signal<DeviceTemplate | null>(null);
  protected readonly templateTags = signal<TemplateTag[]>([]);
  protected readonly newTemplateName = signal('');
  protected readonly templateTagDraft = signal<TemplateTagDraft | null>(null);
  protected readonly instantiateDraft = signal<InstantiateDraft | null>(null);

  /** What a template edit did, kept visible because it changed every instance. */
  protected readonly propagationNote = signal<string | null>(null);

  protected readonly tagAlarm = signal<AlarmDefinition | null>(null);
  protected readonly alarmDraft = signal<{ high: string; low: string } | null>(null);

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

    this.tagAlarm.set(null);
    this.alarmDraft.set(null);

    if (selection.tag?.valueKind === 'Numeric') {
      await this.loadHistory();
      await this.loadTagAlarm();
    }
  }

  // ---- alarms -------------------------------------------------------------

  protected async acknowledge(alarm: Alarm): Promise<void> {
    await this.withErrorHandling(() => this.api.acknowledge(alarm.definitionId).then(() => undefined));
  }

  protected async shelve(alarm: Alarm): Promise<void> {
    await this.withErrorHandling(() => this.api.shelve(alarm.definitionId).then(() => undefined));
  }

  private async loadTagAlarm(): Promise<void> {
    const tag = this.selection()?.tag;
    if (!tag) {
      return;
    }

    try {
      const definitions = await this.api.alarmsOf(tag.id);
      this.tagAlarm.set(definitions[0] ?? null);
    } catch (error) {
      this.report(error);
    }
  }

  protected startEditingAlarm(): void {
    const existing = this.tagAlarm();
    this.alarmDraft.set({
      high: existing?.highLimit?.toString() ?? '',
      low: existing?.lowLimit?.toString() ?? '',
    });
  }

  protected async saveAlarm(): Promise<void> {
    const tag = this.selection()?.tag;
    const draft = this.alarmDraft();

    if (!tag || !draft) {
      return;
    }

    await this.withErrorHandling(async () => {
      // An empty box means "no limit on this side", which is different from zero — and
      // zero is a perfectly ordinary threshold, so the two must not collapse together.
      const toLimit = (raw: string): number | null =>
        raw.trim().length === 0 ? null : Number(raw);

      await this.api.saveAlarm(tag.id, this.tagAlarm()?.id ?? null, {
        highLimit: toLimit(draft.high),
        lowLimit: toLimit(draft.low),
      });

      this.alarmDraft.set(null);
      await this.loadTagAlarm();
    });
  }

  protected async removeAlarm(): Promise<void> {
    const tag = this.selection()?.tag;
    const definition = this.tagAlarm();

    if (!tag || !definition) {
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.deleteAlarm(tag.id, definition.id);
      this.tagAlarm.set(null);
      this.alarmDraft.set(null);
    });
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
      settings: [
        { key: 'host', value: '127.0.0.1' },
        { key: 'port', value: '5502' },
        { key: 'unitId', value: '1' },
      ],
      scanIntervalMs: 1000,
      folderId: null,
    });
  }

  protected editDevice(device: TreeDevice): void {
    this.deviceDraft.set({
      id: device.id,
      name: device.name,
      driverKey: device.driverKey,
      settings: mapToSettings(device.connectionSettings),
      scanIntervalMs: device.scanIntervalMs,
      folderId: device.folderId,
    });
  }

  protected addSetting(entries: SettingEntry[]): void {
    entries.push({ key: '', value: '' });
  }

  protected removeSetting(entries: SettingEntry[], index: number): void {
    entries.splice(index, 1);
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
        connectionSettings: settingsToMap(draft.settings),
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

  // ---- templates ----------------------------------------------------------

  protected async showTemplates(): Promise<void> {
    this.view.set('templates');
    await this.withErrorHandling(async () => {
      this.templates.set(await this.api.templates());
    });
  }

  protected async selectTemplate(template: DeviceTemplate): Promise<void> {
    this.selectedTemplate.set(template);
    this.templateTagDraft.set(null);
    this.instantiateDraft.set(null);
    this.propagationNote.set(null);

    await this.withErrorHandling(async () => {
      this.templateTags.set(await this.api.templateTags(template.id));
    });
  }

  protected async createTemplate(): Promise<void> {
    const name = this.newTemplateName().trim();
    if (name.length === 0) {
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.createTemplate(name);
      this.newTemplateName.set('');
      this.templates.set(await this.api.templates());
    });
  }

  protected startNewTemplateTag(): void {
    this.templateTagDraft.set({
      name: '',
      valueKind: 'Numeric',
      unitSymbol: 'bar',
      addressTemplate: 'holding:{offset}?scale=0.01',
    });
  }

  protected async saveTemplateTag(): Promise<void> {
    const template = this.selectedTemplate();
    const draft = this.templateTagDraft();

    if (!template || !draft) {
      return;
    }

    await this.withErrorHandling(async () => {
      const result = await this.api.addTemplateTag(template.id, {
        name: draft.name,
        valueKind: draft.valueKind,
        unit: draft.valueKind === 'Numeric' ? unitBySymbol(draft.unitSymbol) : null,
        addressTemplate: draft.addressTemplate,
        isWritable: false,
      });

      // Said out loud because it is not obvious: this edit reached every device made
      // from the template, with no confirmation step (ADR-0010).
      this.propagationNote.set(
        `Added to the template and to ${result.instancesUpdated} existing device(s).`,
      );

      this.templateTagDraft.set(null);
      this.templateTags.set(await this.api.templateTags(template.id));
      await this.reloadTree();
    });
  }

  protected async removeTemplateTag(tag: TemplateTag): Promise<void> {
    const template = this.selectedTemplate();
    if (!template) {
      return;
    }

    await this.withErrorHandling(async () => {
      const result = await this.api.deleteTemplateTag(template.id, tag.id);

      this.propagationNote.set(
        `Removed from the template and from ${result.instancesUpdated} existing device(s). ` +
          'Recorded history is kept.',
      );

      this.templateTags.set(await this.api.templateTags(template.id));
      await this.reloadTree();
    });
  }

  /** Every parameter the selected template's addresses need, asked for exactly once. */
  protected readonly requiredParameters = computed(() => {
    const names = new Set<string>();
    for (const tag of this.templateTags()) {
      for (const name of tag.parameterNames) {
        names.add(name);
      }
    }
    return [...names];
  });

  protected startInstantiate(): void {
    const template = this.selectedTemplate();
    if (!template) {
      return;
    }

    this.instantiateDraft.set({
      templateId: template.id,
      name: '',
      driverKey: 'modbus-tcp',
      settings: [
        { key: 'host', value: '127.0.0.1' },
        { key: 'port', value: '5502' },
        { key: 'unitId', value: '1' },
      ],
      scanIntervalMs: 1000,
      folderId: null,
      // Prompting for exactly the template's own placeholders beats a free-form box:
      // a missing one is refused by the gateway anyway, so ask for it up front.
      parameters: this.requiredParameters().map((name) => ({ key: name, value: '' })),
    });
  }

  protected async saveInstance(): Promise<void> {
    const siteId = this.siteId();
    const draft = this.instantiateDraft();

    if (!siteId || !draft) {
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.instantiate(siteId, {
        templateId: draft.templateId,
        name: draft.name,
        driverKey: draft.driverKey,
        connectionSettings: settingsToMap(draft.settings),
        scanIntervalMs: draft.scanIntervalMs,
        folderId: draft.folderId,
        parameters: settingsToMap(draft.parameters),
      });

      this.instantiateDraft.set(null);
      this.propagationNote.set(`Created "${draft.name}" from the template.`);
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
