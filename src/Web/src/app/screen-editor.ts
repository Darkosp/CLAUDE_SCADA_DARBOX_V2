import { Component, computed, effect, input, linkedSignal, output } from '@angular/core';
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
  SYMBOL_STATES,
  SymbolShape,
  SymbolStateRule,
  toSaveScreen,
  trendTagIds,
} from './screen';

/** The six kinds, with what each needs, for the author's picker. The server refuses any other. */
const KINDS: {
  kind: ScreenComponentKind;
  label: string;
  needsTag: boolean;
  /** Whether this kind is nothing but its text, so the server refuses it without any. */
  needsTitle: boolean;
  /** Whether this kind may carry a heading. Optional text: the server accepts it empty. */
  wantsTitle: boolean;
  /**
   * Whether this kind draws a symbol and so needs a state mapping (ADR-0027).
   *
   * The server refuses a `symbol` without one, and the rule lives here as well because the editor is
   * what stops an author reaching that refusal: a kind that needs a mapping gets the mapping form, and
   * a new `symbol` is born with one rather than empty (`newComponent`).
   */
  needsStates: boolean;
}[] = [
  { kind: 'label', label: 'Text', needsTag: false, needsTitle: true, wantsTitle: true, needsStates: false },
  { kind: 'value', label: 'Value', needsTag: true, needsTitle: false, wantsTitle: false, needsStates: false },
  { kind: 'trend', label: 'Trend', needsTag: true, needsTitle: false, wantsTitle: false, needsStates: false },
  { kind: 'status', label: 'Status', needsTag: true, needsTitle: false, wantsTitle: false, needsStates: false },
  // The heading over a Site's alarms. Optional, and it was required until a walk found what that
  // cost: ADR-0024's kinds table gives text as what a `label` shows and says nothing of the sort for
  // `alarms`, and requiring one made an author invent a heading -- and made every Site's seeded
  // screen unsaveable, because the seeder had never supplied one.
  { kind: 'alarms', label: 'Alarms', needsTag: false, needsTitle: false, wantsTitle: true, needsStates: false },
  // A picture of equipment, drawn in a state its mapping derives from its tag (ADR-0027). The first
  // kind that is not a reading rendered some way -- and the first whose configuration is a rule list
  // rather than a field, which is why it has a form of its own below.
  { kind: 'symbol', label: 'Symbol', needsTag: true, needsTitle: false, wantsTitle: true, needsStates: true },
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

        @if (chosen().wantsTitle) {
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

                @if (wantsTitle(cell.kind)) {
                  <input type="text" [ngModel]="cell.title ?? ''"
                         (ngModelChange)="retitle(cell.id, $event)" placeholder="Text" />
                }

                @if (needsStates(cell.kind)) {
                  <!--
                    The state mapping (ADR-0027). This is the first component whose configuration is a
                    RULE LIST rather than a field, so it is the first that needs a form of its own.

                    Three inputs per rule and no expression box: the server evaluates "is", "above" and
                    "below" and refuses anything else, which is the decision ADR-0027 makes so that an
                    author cannot need a debugger to find out what their screen does.
                  -->
                  <div class="mapping">
                    <label class="shape">
                      <span>Symbol</span>
                      <select [ngModel]="cell.symbol ?? 'pump'"
                              (ngModelChange)="reshape(cell.id, $event)">
                        @for (shape of shapes; track shape) {
                          <option [value]="shape">{{ shape }}</option>
                        }
                      </select>
                    </label>

                    @for (rule of cell.states; track $index) {
                      <div class="rule">
                        <select [ngModel]="rule.state"
                                (ngModelChange)="restate(cell.id, $index, $event)"
                                [attr.aria-label]="'State for rule ' + ($index + 1)">
                          @for (state of statesFor(cell.symbol); track state) {
                            <option [value]="state">{{ state }}</option>
                          }
                        </select>

                        <!-- A fallback matches anything, so it has no comparison to state. The server
                             refuses a rule that claims both, and the blank option is how an author
                             says "anything else". -->
                        <select [ngModel]="rule.otherwise ? '' : (rule.when ?? 'equals')"
                                (ngModelChange)="recompare(cell.id, $index, $event)"
                                [attr.aria-label]="'Comparison for rule ' + ($index + 1)">
                          <option value="equals">is</option>
                          <option value="above">above</option>
                          <option value="below">below</option>
                          <option value="">anything else</option>
                        </select>

                        <input type="text" [ngModel]="rule.value ?? ''"
                               (ngModelChange)="revalue(cell.id, $index, $event)"
                               [disabled]="rule.otherwise"
                               [attr.aria-label]="'Value for rule ' + ($index + 1)"
                               placeholder="value" />

                        <button type="button" class="ghost" (click)="removeRule(cell.id, $index)"
                                title="Remove this rule">✕</button>
                      </div>
                    }

                    <button type="button" class="ghost add-rule" (click)="addRule(cell.id)">
                      Add a state
                    </button>
                  </div>
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
                      [history]="history()"
                      (writeRequested)="writeRequested.emit($event)" />        }
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

      <!--
        Phase 8's walk step 6, closed. The walk was written to catch an author being surprised that a
        save is immediate, and the wording was deliberately left until someone had been. This is that
        wording: a save replaces what every operator on this Site sees, at once, with no "publish"
        step and no way back except Cancel -- which only works until you press Save.

        It is a sentence rather than a warning box because it is not a hazard: it is how a screen
        editor has to work if editing is not to need its own version of every screen. What was
        missing was saying so.
      -->
      <p class="muted save-note">
        Saving replaces the screen every operator on this Site sees, immediately. There is no
        separate publish step, and Cancel will not undo it afterwards.
      </p>
    </div>
  `,
  styles: `
    :host { display: block; }
    .name, .controls label { display: block; }
    .name span, .controls span { display: block; font-size: var(--text-sm); color: var(--text-muted); }
    input[type='text'], select {
      width: 100%;
      padding: 5px 7px;
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      background: var(--surface);
      color: var(--text);
    }
    /* The author's controls are grouped in a sunken panel, so the draft they act on reads as the
       thing being worked on rather than as more chrome. */
    .controls {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
      gap: 10px;
      align-items: end;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--surface-sunken);
      padding: 10px 12px 12px;
      margin: 0 0 14px;
    }
    .controls legend { font-size: var(--text-sm); color: var(--text-muted); padding: 0 4px; }
    .name { margin-bottom: 10px; }
    .row {
      display: grid;
      grid-template-columns: repeat(12, 1fr);
      gap: 10px;
    }
    /* Dashed, because this is the author's draft and not what an operator sees: the preview below
       is the operator's view, and the two must not look alike. */
    .cell {
      border: 1px dashed var(--border-strong);
      border-radius: var(--radius);
      padding: 8px 10px;
      min-width: 0;
      background: var(--surface);
    }
    .cell-head { margin: 0 0 6px; font-size: var(--text-base); }
    .path { color: var(--text-muted); margin-left: 6px; }
    .cell-controls { margin: 6px 0 0; display: flex; gap: 4px; align-items: center; }
    .cell-controls button { padding: 1px 7px; }
    .span { font-size: var(--text-xs); color: var(--text-muted); }
    /* The state mapping's form (ADR-0027). Sunken and bounded, because it belongs to the component
       above it rather than to the screen -- and it is the only part of a draft cell that can be
       several rows tall, so it needs to read as one block. */
    .mapping {
      display: grid;
      gap: 4px;
      margin: 6px 0 0;
      padding: 6px 7px 7px;
      background: var(--surface-sunken);
      border-radius: var(--radius-sm);
    }
    .mapping .shape { display: flex; align-items: center; gap: 5px; font-size: var(--text-xs); }
    .mapping .shape select { flex: 1; }
    .rule { display: grid; grid-template-columns: 1fr 1fr 1fr auto; gap: 4px; align-items: center; }
    .rule select, .rule input { font-size: var(--text-xs); padding: 3px 5px; }
    .rule button { padding: 0 5px; }
    .add-rule { font-size: var(--text-xs); padding: 2px 7px; justify-self: start; }
    .row-controls {
      display: flex;
      gap: 8px;
      align-items: center;
      margin: 4px 0 14px;
      font-size: 0.76rem;
    }
    .actions { display: flex; gap: 8px; margin-top: 8px; }
    .save-note { font-size: 0.76rem; margin: 8px 0 0; max-width: 46rem; color: var(--text-muted); }
    .danger { color: var(--status-bad-ink); }
    .problem { color: var(--status-bad-ink); margin: 6px 0; }
    .muted { color: var(--text-muted); }
    .preview {
      margin-top: 18px;
      padding: 12px 14px 14px;
      background: var(--surface-sunken);
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
    }
    .preview-head { display: flex; gap: 10px; align-items: baseline; margin: 0 0 10px; }
    .preview-head .muted { font-size: var(--text-sm); }
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
   * History for the preview's `trend` components, **by tag id**.
   *
   * Keyed by tag rather than by component, and that is what lets the preview draw a real trend.
   * Phase 8's walk recorded the limitation this removes: a component an author has just added has an
   * id the server has never seen, so history keyed by component id could only ever say "Reading…".
   * The history of a trend is the history of its TAG, so the tag is also the more honest key.
   */
  readonly history = input<ReadonlyMap<string, HistorySample[]>>(new Map());

  /**
   * Which tags the draft's `trend` components are bound to, so the app knows what to fetch.
   *
   * A set of ids rather than the draft itself, because the draft lives in this component and
   * lifting it up so that another component could read it would move the author's unsaved work into
   * the app's state for no reason. This is the smallest thing the app has to know to feed the
   * preview, and it changes only when a binding changes — so typing a title does not refetch every
   * series on the screen.
   */
  readonly trendTagsChanged = output<readonly string[]>();

  /**
   * A write an operator confirmed in the preview (ADR-0026).
   *
   * Forwarded rather than performed here, because the editor calls no API: a save goes out as
   * `saveRequested` and an operating write goes out as this, and the app is the one place that
   * knows how to reach the server for either.
   */
  readonly writeRequested = output<{ tagId: string; value: number | boolean }>();

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

  /**
   * Tells the app which tags the draft's trends are bound to, whenever that set changes.
   *
   * An effect rather than a call beside every edit, because there are a dozen ways the draft can
   * change and one of them would eventually be missed — a component removed, a binding changed, a
   * whole screen replaced by a save coming back. Watching the computed set means there is one place
   * that can be right.
   */
  private readonly announceTrendTags = effect(() => {
    this.trendTagsChanged.emit(trendTagIds(this.draft().components));
  });

  /** Whether anything has changed since the server's version, so Save can be disabled. */
  protected readonly changed = computed(
    () => JSON.stringify(this.draft()) !== JSON.stringify(this.screen()),
  );

  protected labelFor(kind: ScreenComponentKind): string {
    return KINDS.find((option) => option.kind === kind)?.label ?? kind;
  }

  protected wantsTitle(kind: ScreenComponentKind): boolean {
    return KINDS.find((option) => option.kind === kind)?.wantsTitle ?? false;
  }

  protected needsStates(kind: ScreenComponentKind): boolean {
    return KINDS.find((option) => option.kind === kind)?.needsStates ?? false;
  }

  /** Every shape this build draws, for the picker. */
  protected readonly shapes = Object.keys(SYMBOL_STATES) as SymbolShape[];

  /** The states one shape can be drawn in, for a rule's state picker. */
  protected statesFor(shape: string | null): readonly string[] {
    return shape !== null && shape in SYMBOL_STATES
      ? SYMBOL_STATES[shape as SymbolShape]
      : [];
  }

  /**
   * Replaces one component's mapping.
   *
   * One place rather than four, because every edit below is "change one rule and keep the rest" and
   * four copies of that is four chances to drop a rule an author did not touch.
   */
  private remap(
    componentId: string,
    change: (states: readonly SymbolStateRule[]) => readonly SymbolStateRule[],
  ): void {
    this.draft.update((draft) => ({
      ...draft,
      components: draft.components.map((component) =>
        component.id === componentId ? { ...component, states: change(component.states) } : component,
      ),
    }));
  }

  /**
   * Changes a symbol's shape.
   *
   * The mapping is kept rather than reset, even though a different shape may not be drawable in a
   * state the mapping names — the server refuses that by name, and discarding an author's rules
   * because they tried the picker would lose work they can see and fix.
   */
  protected reshape(componentId: string, shape: string): void {
    this.draft.update((draft) => ({
      ...draft,
      components: draft.components.map((component) =>
        component.id === componentId ? { ...component, symbol: shape as SymbolShape } : component,
      ),
    }));
  }

  protected restate(componentId: string, at: number, state: string): void {
    this.remap(componentId, (states) =>
      states.map((rule, index) => (index === at ? { ...rule, state } : rule)),
    );
  }

  /**
   * Changes a rule's comparison.
   *
   * An empty choice means the fallback, and **it clears the comparison and the value with it**: the
   * server refuses a fallback that also states what it matches, because a rule that matches anything
   * cannot also say what — and leaving the old text behind would show an author a comparison that is
   * no longer doing anything. It also clears the flag when a comparison is chosen, so a rule cannot be
   * both.
   */
  protected recompare(componentId: string, at: number, when: string): void {
    const fallback = when === '';

    this.remap(componentId, (states) =>
      states.map((rule, index) =>
        index === at
          ? {
              ...rule,
              otherwise: fallback,
              when: fallback ? null : when,
              value: fallback ? null : (rule.value ?? ''),
            }
          : rule,
      ),
    );
  }

  protected revalue(componentId: string, at: number, value: string): void {
    this.remap(componentId, (states) =>
      states.map((rule, index) => (index === at ? { ...rule, value } : rule)),
    );
  }

  protected removeRule(componentId: string, at: number): void {
    this.remap(componentId, (states) => states.filter((_, index) => index !== at));
  }

  /**
   * Adds a rule, pre-filled with a comparison rather than as a fallback.
   *
   * A new fallback would silently take over from whatever the author had, since a fallback matches
   * anything — the one edit here that can change what an existing screen does without being asked.
   */
  protected addRule(componentId: string): void {
    this.remap(componentId, (states) => [
      ...states,
      { state: this.statesFor(this.symbolOf(componentId))[0] ?? 'unknown', when: 'equals', value: '', otherwise: false },
    ]);
  }

  private symbolOf(componentId: string): string | null {
    return this.draft().components.find((component) => component.id === componentId)?.symbol ?? null;
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

    if (option.needsTitle && title.trim() === '') {
      this.problem.set(`A ${option.label.toLowerCase()} component shows text, so it needs some.`);
      return;
    }

    this.draft.update((draft) => ({
      ...draft,
      components: addComponent(
        draft.components,
        newComponent(option.kind, option.needsTag ? tagId : null, option.wantsTitle ? title : null),
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
