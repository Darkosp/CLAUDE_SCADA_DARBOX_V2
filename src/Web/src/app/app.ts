import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Api, ApiError } from './api';
import { Auth } from './auth';
import { BrowseTree, Selection } from './browse-tree';
import { TrendChart } from './trend-chart';
import {
  Access,
  Alarm,
  AlarmDefinition,
  AlarmEvent,
  DeviceTemplate,
  FolderOption,
  HistorySample,
  NumberField,
  SettingEntry,
  Site,
  SiteRole,
  SiteTree,
  TemplateTag,
  TreeDevice,
  folderOptions,
  mapToSettings,
  parseNumberField,
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
  /** An emptied box makes this null, so it is read back through parseNumberField. */
  scanIntervalMs: NumberField;
  folderId: string | null;
}

/** A device being created from a template, with the parameters that template asks for. */
interface InstantiateDraft {
  templateId: string;
  name: string;
  driverKey: string;
  settings: SettingEntry[];
  /** An emptied box makes this null, so it is read back through parseNumberField. */
  scanIntervalMs: NumberField;
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
  kind: 'folder' | 'device' | 'tag' | 'user';
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
  isWritable: boolean;
}

interface UserDraft {
  username: string;
  password: string;
  isAdmin: boolean;
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

  /**
   * What is shown or offered follows the user's roles, but that is a courtesy: the gateway
   * refuses whatever the role does not allow, whatever this component decides to show.
   */
  protected readonly auth = inject(Auth);

  protected readonly signedIn = this.auth.signedIn;
  protected readonly isAdmin = this.auth.isAdmin;
  protected readonly signedInUserId = computed(() => this.auth.access()?.userId ?? null);

  protected readonly loginName = signal('');
  protected readonly loginPassword = signal('');
  protected readonly loginError = signal<string | null>(null);
  protected readonly signingIn = signal(false);

  /** A remembered session being checked with the gateway before the app is shown. */
  protected readonly restoring = signal(false);

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

  /** Which part of the app is on screen. Templates and Users exist only for an Admin. */
  protected readonly view = signal<'browse' | 'templates' | 'users' | 'journal'>('browse');

  /** The journal as last read. Not live: history does not change under the reader. */
  protected readonly journalEvents = signal<AlarmEvent[]>([]);

  protected readonly journalLoading = signal(false);

  /** The user's role on the Site being browsed, for the header. */
  protected readonly roleHere = computed(() => {
    const access = this.auth.access();
    if (!access) {
      return '';
    }

    return access.isAdmin ? 'Admin' : (access.sites.find((grant) => grant.siteId === this.siteId())?.role ?? '');
  });

  protected readonly canOperateHere = computed(() => this.auth.canOperate(this.siteId()));

  protected readonly templates = signal<DeviceTemplate[]>([]);
  protected readonly selectedTemplate = signal<DeviceTemplate | null>(null);
  protected readonly templateTags = signal<TemplateTag[]>([]);
  protected readonly newTemplateName = signal('');
  protected readonly templateTagDraft = signal<TemplateTagDraft | null>(null);
  protected readonly instantiateDraft = signal<InstantiateDraft | null>(null);

  /** What a template edit did, kept visible because it changed every instance. */
  protected readonly propagationNote = signal<string | null>(null);

  protected readonly tagAlarm = signal<AlarmDefinition | null>(null);
  protected readonly alarmDraft = signal<{ high: NumberField; low: NumberField } | null>(null);

  protected readonly writeDraft = signal('');
  protected readonly writeNote = signal<string | null>(null);

  protected readonly users = signal<Access[]>([]);
  protected readonly userDraft = signal<UserDraft | null>(null);
  protected readonly passwordReset = signal<{ userId: string; username: string; password: string } | null>(null);

  protected readonly folderChoices = computed<FolderOption[]>(() => folderOptions(this.tree()));

  /** The live value of the selected tag, or null before its first reading. */
  protected readonly liveValue = computed<TagSnapshot | null>(() => {
    const tagId = this.selection()?.tag?.id;
    return tagId ? (this.stream.tags().find((snapshot) => snapshot.tagId === tagId) ?? null) : null;
  });

  protected readonly selectedIsNumeric = computed(() => this.selection()?.tag?.valueKind === 'Numeric');

  constructor() {
    // Whatever ends the session — sign-out, or a 401 because it expired or was revoked —
    // the live connection closes and nothing the previous user could see stays on screen
    // for whoever signs in next.
    effect(() => {
      if (!this.auth.signedIn()) {
        untracked(() => {
          void this.stream.stop();
          this.resetView();
        });
      }
    });

    // The gateway drops a connection whose session has ended. When that happens, find out
    // whether the session is really over rather than sitting on a dead connection.
    this.stream.onClosed = () => void this.checkSession();

    // Values from a Site no longer permitted stop arriving on their own; this clears what was
    // already on screen for it — its tree, a selected tag, a fetched trend.
    this.stream.onAccessChanged = () => void this.refreshAccess();
  }

  async ngOnInit(): Promise<void> {
    if (!this.auth.token()) {
      return;
    }

    this.restoring.set(true);
    try {
      this.auth.access.set(await this.api.me());
      await this.enter();
    } catch (error) {
      if (!(error instanceof ApiError && error.status === 401)) {
        this.loginError.set('The gateway could not be reached. Sign in again once it is running.');
      }
      this.auth.end();
    } finally {
      this.restoring.set(false);
    }
  }

  protected format(snapshot: TagSnapshot): string {
    return formatValue(snapshot);
  }

  // ---- session ------------------------------------------------------------

  protected async login(): Promise<void> {
    this.loginError.set(null);
    this.signingIn.set(true);

    try {
      const session = await this.api.login(this.loginName().trim(), this.loginPassword());
      this.auth.begin(session.token, session.access);
      this.loginPassword.set('');
      await this.enter();
    } catch (error) {
      this.loginError.set(error instanceof ApiError ? error.message : 'The gateway could not be reached.');
    } finally {
      this.signingIn.set(false);
    }
  }

  protected async logout(): Promise<void> {
    try {
      // Ends the session on the gateway, not just in this browser, so the token stops
      // working everywhere at once.
      await this.api.logout();
    } catch {
      // Signed out locally regardless.
    }

    this.auth.end();
  }

  private async enter(): Promise<void> {
    void this.stream.start();
    await this.loadSites();
  }

  private async checkSession(): Promise<void> {
    if (!this.auth.token()) {
      return;
    }

    try {
      this.auth.access.set(await this.api.me());
    } catch {
      // A 401 has already ended the session through the API client. Anything else — the
      // gateway being unreachable — leaves the operator signed in to try again.
    }
  }

  /**
   * Brings the whole screen in line with the user's current roles after the gateway says
   * they changed.
   */
  private async refreshAccess(): Promise<void> {
    try {
      this.auth.access.set(await this.api.me());

      const sites = await this.api.sites();
      this.sites.set(sites);

      if (sites.some((site) => site.id === this.siteId())) {
        await this.reloadTree();
      } else {
        // The Site on screen is no longer this user's. Nothing loaded for it while it was
        // permitted may stay visible.
        this.siteId.set(null);
        this.tree.set(null);
        this.selection.set(null);
        this.history.set([]);
        this.tagAlarm.set(null);
        this.alarmDraft.set(null);
        this.writeNote.set(null);

        if (sites.length > 0) {
          await this.selectSite(sites[0].id);
        }
      }

      // Templates and Users are Admin screens; someone just demoted must not stay on one.
      // The journal is not one of them: every signed-in user may read it, filtered to
      // their own Sites by the Gateway (ADR-0011, ADR-0013).
      if (!this.auth.isAdmin() && this.view() !== 'browse' && this.view() !== 'journal') {
        this.view.set('browse');
      }
    } catch (error) {
      this.report(error);
    }
  }

  private resetView(): void {
    this.sites.set([]);
    this.siteId.set(null);
    this.tree.set(null);
    this.selection.set(null);
    this.history.set([]);
    this.error.set(null);
    this.view.set('browse');
    this.pendingDelete.set(null);
    this.deviceDraft.set(null);
    this.tagDraft.set(null);
    this.alarmDraft.set(null);
    this.users.set([]);
    this.userDraft.set(null);
    this.passwordReset.set(null);
    this.templates.set([]);
    this.selectedTemplate.set(null);
  }

  // ---- browsing -----------------------------------------------------------

  protected async loadSites(): Promise<void> {
    try {
      const sites = await this.api.sites();
      this.sites.set(sites);

      if (sites.length > 0 && !sites.some((site) => site.id === this.siteId())) {
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
    this.writeDraft.set(selection.tag?.valueKind === 'Boolean' ? 'true' : '');
    this.writeNote.set(null);

    this.tagAlarm.set(null);
    this.alarmDraft.set(null);

    if (selection.tag?.valueKind === 'Numeric') {
      await this.loadHistory();
      await this.loadTagAlarm();
    }
  }

  // ---- writing a tag (Operator) -------------------------------------------

  protected async writeValue(): Promise<void> {
    const tag = this.selection()?.tag;
    const raw = this.writeDraft().trim();

    if (!tag || raw.length === 0) {
      return;
    }

    let value: number | boolean | string;

    if (tag.valueKind === 'Boolean') {
      value = raw === 'true';
    } else if (tag.valueKind === 'Text') {
      value = raw;
    } else {
      value = Number(raw);
      if (!Number.isFinite(value)) {
        this.error.set('Enter a number.');
        return;
      }
    }

    await this.withErrorHandling(async () => {
      await this.api.writeTag(tag.id, value);
      this.writeNote.set(`Sent ${raw} to ${tag.name}. The next scan shows what the device holds.`);
      if (tag.valueKind !== 'Boolean') {
        this.writeDraft.set('');
      }
    });
  }

  // ---- alarms -------------------------------------------------------------

  protected async acknowledge(alarm: Alarm): Promise<void> {
    await this.withErrorHandling(() => this.api.acknowledge(alarm.definitionId).then(() => undefined));
  }

  /**
   * What an operator may pick from. Presets rather than a free field: a shelf is a
   * deliberate, bounded silence, and typing a number invites both a slip of the keyboard
   * and a duration nobody meant. The server's maximum (ADR-0013) is the real limit — this
   * list stays inside it, but is not what enforces it.
   */
  protected readonly shelveChoices: ReadonlyArray<{ minutes: number; label: string }> = [
    { minutes: 5, label: '5 min' },
    { minutes: 15, label: '15 min' },
    { minutes: 60, label: '1 h' },
    { minutes: 240, label: '4 h' },
    { minutes: 480, label: '8 h' },
    { minutes: 1440, label: '24 h' },
  ];

  /** The duration chosen per alarm, by definition id. Nothing is chosen until it is. */
  private readonly shelveMinutes = signal<Record<string, number>>({});

  protected chosenShelve(alarm: Alarm): number | null {
    return this.shelveMinutes()[alarm.definitionId] ?? null;
  }

  protected chooseShelve(alarm: Alarm, minutes: string): void {
    const chosen = Number(minutes);

    this.shelveMinutes.update(current => ({
      ...current,
      [alarm.definitionId]: chosen,
    }));
  }

  /**
   * Shelves for the chosen duration. There is no fallback: with nothing chosen this does
   * nothing, so a shelf is never longer — or shorter — than someone actually asked for.
   */
  protected async shelve(alarm: Alarm): Promise<void> {
    const minutes = this.chosenShelve(alarm);
    if (minutes === null) {
      return;
    }

    await this.withErrorHandling(() => this.api.shelve(alarm.definitionId, minutes).then(() => undefined));
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

    // An empty box means "no limit on this side", which is different from zero — and
    // zero is a perfectly ordinary threshold, so the two must not collapse together.
    const high = parseNumberField(draft.high);
    const low = parseNumberField(draft.low);

    if (!high.ok || !low.ok) {
      this.error.set('A limit must be a number, or blank for no limit on that side.');
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.saveAlarm(tag.id, this.tagAlarm()?.id ?? null, {
        highLimit: high.value,
        lowLimit: low.value,
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

    // Unlike a limit, a scan interval has no "blank means none": a device that is not
    // scanned is not a device. Caught here so an emptied box says so, rather than
    // travelling to the Gateway as null and coming back as a validation error.
    const scan = parseNumberField(draft.scanIntervalMs);
    if (!scan.ok || scan.value === null) {
      this.error.set('Scan interval must be a number of milliseconds.');
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.saveDevice(siteId, draft.id, {
        name: draft.name,
        driverKey: draft.driverKey,
        connectionSettings: settingsToMap(draft.settings),
        scanIntervalMs: scan.value,
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
      isWritable: false,
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
        isWritable: draft.isWritable,
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

  protected askDeactivateUser(user: Access): void {
    this.pendingDelete.set({
      kind: 'user',
      id: user.userId,
      ownerId: '',
      label: `user "${user.username}"`,
      warning: 'They are signed out everywhere at once and cannot sign in again. What they did stays in the audit trail.',
    });
  }

  protected async confirmDelete(): Promise<void> {
    const pending = this.pendingDelete();
    if (!pending) {
      return;
    }

    await this.withErrorHandling(async () => {
      if (pending.kind === 'user') {
        await this.api.deactivateUser(pending.id);
        this.pendingDelete.set(null);
        await this.loadUsers();
        return;
      }

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

  // ---- templates (Admin) --------------------------------------------------

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

    const scan = parseNumberField(draft.scanIntervalMs);
    if (!scan.ok || scan.value === null) {
      this.error.set('Scan interval must be a number of milliseconds.');
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.instantiate(siteId, {
        templateId: draft.templateId,
        name: draft.name,
        driverKey: draft.driverKey,
        connectionSettings: settingsToMap(draft.settings),
        scanIntervalMs: scan.value,
        folderId: draft.folderId,
        parameters: settingsToMap(draft.parameters),
      });

      this.instantiateDraft.set(null);
      this.propagationNote.set(`Created "${draft.name}" from the template.`);
      await this.reloadTree();
    });
  }

  // ---- users (Admin) ------------------------------------------------------

  protected async showUsers(): Promise<void> {
    this.view.set('users');
    await this.withErrorHandling(() => this.loadUsers());
  }
  /** Opens the journal and reads it once. */
  protected async showJournal(): Promise<void> {
    this.view.set('journal');
    this.journalLoading.set(true);

    try {
      this.journalEvents.set(await this.api.journal());
    } catch (error) {
      this.report(error);
    } finally {
      this.journalLoading.set(false);
    }
  }

  /**
   * Whether an entry is the engine speaking rather than an alarm: it names no occurrence.
   * Those rows carry no Site, and are shown to every reader on purpose (ADR-0013).
   */
  protected isEngineEvent(entry: AlarmEvent): boolean {
    return entry.occurrenceId === null;
  }


  private async loadUsers(): Promise<void> {
    this.users.set(await this.api.users());
  }

  protected roleOn(user: Access, siteId: string): SiteRole | '' {
    return user.sites.find((grant) => grant.siteId === siteId)?.role ?? '';
  }

  protected startNewUser(): void {
    this.passwordReset.set(null);
    this.userDraft.set({ username: '', password: '', isAdmin: false });
  }

  protected async saveUser(): Promise<void> {
    const draft = this.userDraft();
    if (!draft) {
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.createUser({ username: draft.username.trim(), password: draft.password, isAdmin: draft.isAdmin });
      this.userDraft.set(null);
      await this.loadUsers();
    });
  }

  protected async changeRole(user: Access, siteId: string, role: SiteRole | ''): Promise<void> {
    await this.withErrorHandling(async () => {
      try {
        if (role === '') {
          await this.api.removeSiteRole(user.userId, siteId);
        } else {
          await this.api.setSiteRole(user.userId, siteId, role);
        }
      } finally {
        // Reloaded either way, so a refused change never leaves the picker showing a role
        // the user does not have.
        await this.afterUserChange(user);
      }
    });
  }

  protected async toggleAdmin(user: Access): Promise<void> {
    await this.withErrorHandling(async () => {
      try {
        await this.api.setAdmin(user.userId, !user.isAdmin);
      } finally {
        await this.afterUserChange(user);
      }
    });
  }

  protected startPasswordReset(user: Access): void {
    this.userDraft.set(null);
    this.passwordReset.set({ userId: user.userId, username: user.username, password: '' });
  }

  protected async savePassword(): Promise<void> {
    const reset = this.passwordReset();
    if (!reset) {
      return;
    }

    await this.withErrorHandling(async () => {
      await this.api.setPassword(reset.userId, reset.password);
      this.passwordReset.set(null);
    });
  }

  private async afterUserChange(user: Access): Promise<void> {
    await this.loadUsers();

    // An Admin changing their own roles changes what this screen may show.
    if (user.userId === this.signedInUserId()) {
      this.auth.access.set(await this.api.me());
    }
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
