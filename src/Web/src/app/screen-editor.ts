import { Component, computed, input, linkedSignal, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Alarm, HistorySample } from './models';
import { TagSnapshot } from './tag';
import { ScreenView } from './screen-view';
import {
  addComponent,
  changeComponent,
  groupIntoRows,
  moveComponent,
  newComponent,
  removeComponent,
  reorderComponent,
  Screen,
  ScreenComponent,
  ScreenComponentKind,
  toSaveScreen,
} from './screen';

/** The five kinds, with what each needs, for the author's picker. The server refuses any other. */
const KINDS: { kind: ScreenComponentKind; label: string; needsTag: boolean; takesTitle: boolean }[] = [
  { kind: 'label', label: 'Text', needsTag: false, takesTitle: true },
  { kind: 'value', label: 'Value', needsTag: true, takesTitle: false },
  { kind: 'trend', label: 'Trend', needsTag: true, takesTitle: false },
  { kind: 'status', label: 'Status', needsTag: true, takesTitle: false },
  { kind: 'alarms', label: 'Alarms', needsTag: false, takesTitle: true },
];

/** The narrowest a reading still fits in, and the widest a screen is. */
const NARROWEST = 1;
const WIDEST = 12;

/**
 * The screen editor: what an author does to a screen (ADR-0024).
 *
 * <b>This edits a draft and hands the whole thing to the API on save.</b> A screen is saved by
 * replacing its component set, so every operation here works on a local copy and nothing leaves
 * until "Save" — which is what makes "Cancel" mean something, and what makes a failed save leave
 * the author's work where it was rather than half-applied on the server.
 *
 * Everything about a component's <i>size and place</i> is done by `screen.ts`'s operations rather
 * than by this component, so the rules about rows, spans and positions live in one tested place and
 * what is here is only what a button says and what the author may type.
 */
@Component({
  selector: 'app-screen-editor',
  imports: [FormsModule, ScreenView],
  template: `
    <div class="editor">
      @if (problem(); as message) {
        <p class="problem">{{ message }}</p>
      }

      <label class="name">
        <span>Name</span>
        <input type="text" [ngModel]="draft().name" (ngModelChange)="rename($event)" />
      </label>

      <fieldset class="controls">
        <legend>Add a component</legend>

        <label>
          <span>Kind</span>
          <select [ngModel]="chosenKind()" (ngModelChange)="picked($event)">
            @for (option of kinds; track option.kind) {
              <option [value]="option.kind">{{ option.label }}</option>
            }
          </select>
        </label>

        @if (chosen().needsTag) {
          <label>
            <span>Tag</span>
            <select [ngModel]="chosenTag()" (ngModelChange)="chosenTag.set($event)">
              <option value="">—</option>
              @for (tag of tags(); track tag.tagId) {
                <option [value]="tag.tagId">{{ tag.path }}</option>
              }
            </select>
          </label>
        }

        @if (chosen().takesTitle) {
          <label class="wide">
            <span>Text</span>
            <input type="text" [ngModel]="chosenTitle()" (ngModelChange)="chosenTitle.set($event)"
                   placeholder="what this component says" />
          </label>
        }

        <button type="button" (click)="add()">Add to row 1</button>
      </fieldset>

      @if (components().length === 0) {
        <p class="muted">Nothing on this screen yet. Add a component above.</p>
      } @else {
        @for (row of rows(); track row.rowIndex) {
          <div class="row">
            @for (cell of row.components; track cell.id) {
              <div class="cell" [style.grid-column]="'span ' + cell.columnSpan">
                <p class="cell-head">
                  <strong>{{ labelFor(cell.kind) }}</strong>
                  @if (cell.tagId) {
                    <span class="path">{{ pathOf(cell.tagId) }}</span>
                  }
                </p>

                @if (takesTitle(cell.kind)) {
                  <input type="text" [ngModel]="cell.title ?? ''"
                         (ngModelChange)="retitle(cell.id, $event)" placeholder="Text" />
                }

                <p class="cell-controls">
                  <button type="button" class="ghost" (click)="reorder(cell.id, -1)" title="Move left">←</button>
                  <button type="button" class="ghost" (click)="reorder(cell.id, 1)" title="Move right">→</button>
                  <button type="button" class="ghost" (click)="narrower(cell)" title="Narrower">−</button>
                  <button type="button" class="ghost" (click)="wider(cell)" title="Wider">+</button>
                  <span class="span">{{ cell.columnSpan }}/12</span>
                  <button type="button" class="ghost" (click)="remove(cell.id)" title="Remove">✕</button>
                </p>
              </div>
            }
          </div>

          <p class="row-controls">
            <span class="muted">row {{ row.rowIndex + 1 }}</span>
            @if (row.rowIndex > 0) {
              <button type="button" class="ghost" (click)="moveRowUp(row.rowIndex)">Move row up</button>
            }
          </p>
        }
      }

      @if (errors() !== null) {
        <p class="problem">{{ errors() }}</p>
      }

      <!--
        What the operator will see, drawn by the operator's own component.

        This is the same app-screen the read view uses, given the draft instead of the saved screen,
        so there is no second renderer to drift: whatever is wrong here is wrong there, and a fix to
        one is a fix to both. The draft's components carry readable: true while an author is working
        on them (see newComponent), which is what lets a binding added a second ago show a value
        rather than "Not available to you" — and it is not a claim the client is allowed to make to the
        server, which is why toSaveScreen drops it.
      -->
      <section class="preview">
        <p class="preview-head">
          <strong>Preview</strong>
          <span class="muted">what an operator on this Site sees — live values, not a mock-up</span>
        </p>
        @if (components().length === 0) {
          <p class="muted">Nothing to preview yet.</p>
        } @else {
          <app-screen [screen]="draft()"
                      [snapshots]="snapshots()"
                      [alarms]="alarms()"
                      [history]="history()" />
        }
      </section>

      <div class="actions">
        <button type="button" (click)="save()" [disabled]="saving() || !changed()">
          {{ saving() ? 'Saving…' : 'Save' }}
        </button>
        <button type="button" class="ghost" (click)="cancel()" [disabled]="saving()">Cancel</button>
        <button type="button" class="ghost danger" (click)="removeScreen()" [disabled]="saving()">
          Delete screen
        </button>
      </div>
    </div>
  `,
  styles: `
    :host { display: block; }
    .name, .controls label { display: block; }
    .name span, .controls span { display: block; font-size: 0.78rem; color: #5b6672; }
    input[type='text'], select {
      width: 100%;
      padding: 5px 7px;
      border: 1px solid #cbd3dc;
      border-radius: 6px;
    }
    .controls {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
      gap: 10px;
      align-items: end;
      border: 1px solid #e3e7ec;
      border-radius: 8px;
      padding: 10px 12px 12px;
      margin: 0 0 14px;
    }
    .controls legend { font-size: 0.78rem; color: #5b6672; padding: 0 4px; }
    .name { margin-bottom: 10px; }
    .row {
      display: grid;
      grid-template-columns: repeat(12, 1fr);
      gap: 10px;
    }
    .cell {
      border: 1px dashed #cbd3dc;
      border-radius: 8px;
      padding: 8px 10px;
      min-width: 0;
      background: #fff;
    }
    .cell-head { margin: 0 0 6px; font-size: 0.82rem; }
    .path { color: #5b6672; margin-left: 6px; }
    .cell-controls { margin: 6px 0 0; display: flex; gap: 4px; align-items: center; }
    .cell-controls button { padding: 1px 7px; }
    .span { font-size: 0.72rem; color: #8a949e; }
    .row-controls {
      display: flex;
      gap: 8px;
      align-items: center;
      margin: 4px 0 14px;
      font-size: 0.76rem;
    }
    .actions { display: flex; gap: 8px; margin-top: 8px; }
    .danger { color: #99201f; }
    .problem { color: #99201f; margin: 6px 0; }
    .muted { color: #5b6672; }
    .preview {
      margin-top: 18px;
      padding: 12px 14px 14px;
      background: #f7f9fb;
      border: 1px solid #dde3ea;
      border-radius: 8px;
    }
    .preview-head { display: flex; gap: 10px; align-items: baseline; margin: 0 0 10px; }
    .preview-head .muted { font-size: 0.78rem; }
  `,
})
export class ScreenEditor {
  /** The screen as the server last had it. The draft starts from this and returns to it on cancel. */
  readonly screen = input.required<Screen>();

  /** Every tag this session may see, for the author to bind to. */
  readonly tags = input.required<readonly TagSnapshot[]>();

  /** The live values the preview draws with — the operator's own inputs, not copies. */
  readonly snapshots = input.required<ReadonlyMap<string, TagSnapshot>>();

  /** Every standing alarm this session may see, for the preview's `alarms` components. */
  readonly alarms = input.required<readonly Alarm[]>();

  /**
   * History for the preview's `trend` components, by component id.
   *
   * Empty while an author is working, and that is honest rather than lazy: history is fetched by the
   * read view for the components of a *saved* screen, and a component added a moment ago has an id
   * the server has never seen. A trend in the preview therefore says "Reading…" — which is what the
   * read view says too, for a screen whose history has not arrived. Wiring a fetch for draft ids
   * would mean asking the server about components that do not exist yet.
   */
  readonly history = input<ReadonlyMap<string, HistorySample[]>>(new Map());

  readonly saving = input(false);

  /** What the server said when a save failed, shown above the buttons rather than over the screen. */
  readonly errors = input<string | null>(null);

  readonly saveRequested = output<ReturnType<typeof toSaveScreen>>();

  readonly deleteRequested = output<void>();

  readonly cancelled = output<void>();

  protected readonly kinds = KINDS;

  /**
   * What the author is editing.
   *
   * `linkedSignal` rather than a plain `signal` with a constructor, because the draft has to follow
   * the screen it is given: an author who opens one screen and then another must not carry the first
   * one's edits across, and a save that comes back with the server's version must replace what is
   * being edited. `Cancel` resets it the same way, by setting it back to the screen.
   */
  protected readonly draft = linkedSignal(() => this.screen());

  /** What the author was told when they tried to add something the server would refuse. */
  protected readonly problem = linkedSignal({
    source: () => this.screen(),
    computation: () => null as string | null,
  });

  /**
   * The three things a component being added is made of, as signals rather than template reference
   * variables.
   *
   * Template variables would do, and they were the first attempt — but they exist only inside the
   * `@if` that declares them, and each of these is inside a different one because a tag picker is
   * shown only for a kind that reads a tag. Signals are visible to the whole template whatever is
   * showing, which is what the Add button needs.
   */
  protected readonly chosenKind = linkedSignal({
    source: () => this.screen(),
    computation: () => 'label' as ScreenComponentKind,
  });

  protected readonly chosenTag = linkedSignal({
    source: () => this.screen(),
    computation: () => '',
  });

  protected readonly chosenTitle = linkedSignal({
    source: () => this.screen(),
    computation: () => '',
  });

  protected readonly chosen = computed(
    () => KINDS.find((option) => option.kind === this.chosenKind()) ?? KINDS[0],
  );

  protected readonly components = computed(() => this.draft().components);

  protected readonly rows = computed(() => groupIntoRows(this.components()));

  /** Whether anything has changed since the server's version, so Save can be disabled. */
  protected readonly changed = computed(
    () => JSON.stringify(this.draft()) !== JSON.stringify(this.screen()),
  );

  protected labelFor(kind: ScreenComponentKind): string {
    return KINDS.find((option) => option.kind === kind)?.label ?? kind;
  }

  protected takesTitle(kind: ScreenComponentKind): boolean {
    return KINDS.find((option) => option.kind === kind)?.takesTitle ?? false;
  }

  protected pathOf(tagId: string): string {
    // A tag the author cannot see is still named here rather than left blank: it is a tag somebody
    // bound once, and "a tag you cannot see" is more use than an empty space.
    return this.tags().find((tag) => tag.tagId === tagId)?.path ?? 'a tag you cannot see';
  }

  protected picked(kind: string): void {
    this.chosenKind.set(kind as ScreenComponentKind);
    this.problem.set(null);
  }

  protected rename(name: string): void {
    this.draft.update((draft) => ({ ...draft, name }));
  }

  protected add(): void {
    this.addTo(0);
  }

  protected addTo(rowIndex: number): void {
    const option = this.chosen();
    const tagId = this.chosenTag();
    const title = this.chosenTitle();
    this.problem.set(null);

    // Refused here as well as by the server, and for the reason the server's refusal exists
    // (ADR-0024 §4): a component added without the thing its kind reads is one that would be refused
    // on save, and an author who has built a screen by then has lost the work of finding that out.
    if (option.needsTag && tagId === '') {
      this.problem.set(`A ${option.label.toLowerCase()} component reads a tag, so it needs one.`);
      return;
    }

    if (option.takesTitle && title.trim() === '') {
      this.problem.set(`A ${option.label.toLowerCase()} component shows text, so it needs some.`);
      return;
    }

    this.draft.update((draft) => ({
      ...draft,
      components: addComponent(
        draft.components,
        newComponent(option.kind, option.needsTag ? tagId : null, option.takesTitle ? title : null),
        rowIndex,
      ),
    }));
  }

  protected remove(componentId: string): void {
    this.edit((components) => removeComponent(components, componentId));
  }

  protected retitle(componentId: string, title: string): void {
    this.edit((components) => changeComponent(components, componentId, { title }));
  }

  protected reorder(componentId: string, direction: -1 | 1): void {
    this.edit((components) => reorderComponent(components, componentId, direction));
  }

  protected narrower(component: ScreenComponent): void {
    this.resize(component, Math.max(NARROWEST, Math.floor(component.columnSpan / 2)));
  }

  protected wider(component: ScreenComponent): void {
    this.resize(component, Math.min(WIDEST, component.columnSpan * 2));
  }

  /**
   * Moves a whole row up by putting each of its components at the top of the row above.
   *
   * Done through `moveComponent` rather than by renumbering rows here, because `screen.ts` is what
   * decides what a row is — a second implementation of that in this component is a second place for
   * it to disagree with the renderer.
   */
  protected moveRowUp(rowIndex: number): void {
    const above = rowIndex - 1;

    this.edit((components) =>
      components.reduce<ScreenComponent[]>(
        (current, component) =>
          component.rowIndex === rowIndex ? moveComponent(current, component.id, above, 0) : current,
        [...components],
      ),
    );
  }

  private resize(component: ScreenComponent, columnSpan: number): void {
    this.edit((components) => changeComponent(components, component.id, { columnSpan }));
  }

  private edit(change: (components: readonly ScreenComponent[]) => ScreenComponent[]): void {
    this.problem.set(null);
    this.draft.update((draft) => ({
      ...draft,
      components: change(draft.components),
    }));
  }

  protected save(): void {
    this.problem.set(null);
    this.saveRequested.emit(toSaveScreen(this.draft()));
  }

  protected cancel(): void {
    this.draft.set(this.screen());
    this.cancelled.emit();
  }

  protected removeScreen(): void {
    this.deleteRequested.emit();
  }
}
