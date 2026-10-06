import { Component, computed, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Alarm, HistorySample } from './models';
import {
  groupIntoRows,
  resolveComponent,
  ResolvedScreenComponent,
  Screen,
  ScreenComponent,
} from './screen';
import { TagSnapshot } from './tag';
import { TrendChart } from './trend-chart';
import { SymbolView } from './symbol';

/**
 * One operator screen: the rows a person looks at (ADR-0024).
 *
 * The whole of the rendering decision is made by `screen.ts`'s resolver, which is pure and tested
 * without a browser. This component only puts the answer on the page, which is why there is no
 * branch here about quality or readability — a template that decided those for itself would be a
 * second place for the honesty rule to live, and the second place is the one that drifts.
 */
@Component({
  selector: 'app-screen',
  imports: [TrendChart, SymbolView, FormsModule],
  template: `
    @if (rows().length === 0) {
      <p class="empty">This screen has nothing on it.</p>
    } @else {
      @for (row of rows(); track row.rowIndex) {
        <div class="row">
          @for (cell of row.components; track cell.component.id) {
            <div class="cell" [style.grid-column]="'span ' + cell.component.columnSpan">
              @switch (cell.resolved.kind) {
                @case ('label') {
                  <h3 class="label">{{ $any(cell.resolved).text }}</h3>
                }
                @case ('value') {
                  <p class="caption">
                    <!--
                      The short label and the full path in the tooltip: a tile is captioned with the
                      tag's own name, and a reader who needs to know which device it is under hovers
                      rather than reading a repeated path on every tile.
                    -->
                    <span class="path" [title]="$any(cell.resolved).path">{{ $any(cell.resolved).caption }}</span>
                    @if ($any(cell.resolved).writable) {
                      <span class="writable" title="This tag can be written">writable</span>
                    }
                    <span class="quality" [class]="'q-' + $any(cell.resolved).quality">
                      {{ $any(cell.resolved).quality }}
                    </span>
                  </p>
                  <p class="reading" [class.zero]="$any(cell.resolved).zero">{{ $any(cell.resolved).text }}</p>

                  <!-- The time appears only when the reading is not fresh, so its presence on a
                       tile is itself the signal rather than one line of chrome among four. -->
                  @if ($any(cell.resolved).note; as note) {
                    <p class="at">{{ note }}</p>
                  }

                  <!--
                    The write control (ADR-0026). Offered only where the server said it was writable,
                    and only on a value component — decision 1's "one component writes, nothing else
                    does". The button is not the guard: the API refuses an unpermitted write whatever
                    a client draws, and the flag's job is to not offer what would be refused.

                    The second condition is not a screen decision but a fact about the tag: a text,
                    discrete or valueless tag has no value this dialog can collect, and offering a
                    numeric box for one would invite a rejected write.
                  -->
                  @if ($any(cell.resolved).writable && writableAsANumberOrAFlag($any(cell.resolved).valueKind)) {
                    <button type="button" class="operate" (click)="beginWrite(cell.resolved)">
                      Write…
                    </button>
                  }
                }
                @case ('status') {
                  <p class="caption">
                    @if ($any(cell.resolved).writable) {
                      <span class="writable" title="This tag can be written, though not from a status component">
                        writable
                      </span>
                    }
                  </p>
                  <p class="quality alone" [class]="'q-' + $any(cell.resolved).quality">
                    {{ $any(cell.resolved).quality }}
                  </p>
                }
                @case ('symbol') {
                  <p class="caption">
                    <span class="path" [title]="$any(cell.resolved).path">{{ $any(cell.resolved).caption }}</span>
                    @if ($any(cell.resolved).fromQuality) {
                      <!--
                        Said out loud, because it is the difference between a machine the plant says is
                        off and one nothing is measuring (ADR-0003). The drawing shows it too; a word is
                        what a reader who does not know this project's colours has to go on.
                      -->
                      <span class="quality" [class]="'q-' + $any(cell.resolved).quality">
                        {{ $any(cell.resolved).quality }}
                      </span>
                    }
                  </p>
                  <app-symbol [shape]="$any(cell.resolved).shape"
                              [state]="$any(cell.resolved).state" />
                  <p class="symbol-state" [class]="'t-' + $any(cell.resolved).state">
                    {{ $any(cell.resolved).state }}
                  </p>
                }
                @case ('trend') {
                  <p class="caption">
                    <span class="path" [title]="$any(cell.resolved).path">{{ $any(cell.resolved).caption }}</span>
                    @if ($any(cell.resolved).writable) {
                      <span class="writable" title="This tag can be written, though not from a trend">
                        writable
                      </span>
                    }
                  </p>
                  @if (historyFor($any(cell.resolved).tagId); as samples) {
                    @if (samples.length < 2) {
                      <p class="muted">Not enough history yet.</p>
                    } @else {
                      <app-trend-chart [samples]="samples"
                                       [unitSymbol]="snapshots().get($any(cell.resolved).tagId)?.unitSymbol ?? ''" />
                    }
                  } @else {
                    <p class="muted">Reading…</p>
                  }
                }
                @case ('alarms') {
                  @if ($any(cell.resolved).title; as heading) {
                    <h3 class="label">{{ heading }}</h3>
                  }
                  @if ($any(cell.resolved).alarms.length === 0) {
                    <p class="muted">No standing alarms.</p>
                  } @else {
                    <ul class="alarms">
                      @for (alarm of $any(cell.resolved).alarms; track alarm.occurrenceId) {
                        <li>
                          <span class="path">{{ alarm.tagPath }}</span>
                          <span class="limit">{{ alarm.limit }}</span>
                          <span class="value">{{ alarm.valueAtRaise }}</span>
                        </li>
                      }
                    </ul>
                  }
                }
                @default {
                  <p class="unavailable">{{ $any(cell.resolved).note }}</p>
                }
              }
            </div>
          }
        </div>
      }
    }

    <!--
      The write dialog (ADR-0026). Inside this component so that the read view and the editor's
      preview share one set of rules about what it shows, when it closes and what it keeps -- the
      same reason one renderer serves both.

      It shows the tag, its reading, its quality and its source time (decision 4). **A Bad or old
      reading does not disable the Write button**: there is a real case for writing to a controller
      that has stopped reporting, and refusing would be this software deciding a plant procedure it
      cannot see. The dialog's job is that the operator is looking at what the plant is doing.
    -->
    @if (writeTarget(); as target) {
      <div class="write-backdrop">
        <form class="write-dialog" (ngSubmit)="confirmWrite()">
          <h3>Write to {{ target.path }}</h3>

          <p class="write-current">
            Now: <strong>{{ target.text }}</strong>
            <span class="quality" [class]="'q-' + target.quality">{{ target.quality }}</span>
            @if (target.sourceTimestampUtc; as at) {
              <span class="muted">at {{ at }}</span>
            }
          </p>

          <label>
            New value
            @if (target.valueKind === 'boolean') {
              <!-- A picker, not free text: there are two answers and neither is a typo away. -->
              <select name="writeValue" [ngModel]="writeDraft()"
                      (ngModelChange)="writeDraft.set($event)" [disabled]="writing()">
                <option value="">Choose…</option>
                <option value="true">true</option>
                <option value="false">false</option>
              </select>
            } @else {
              <input name="writeValue" type="text" inputmode="decimal" autocomplete="off"
                     [ngModel]="writeDraft()" (ngModelChange)="writeDraft.set($event)"
                     [disabled]="writing()" />
            }
          </label>

          @if (writeProblem(); as problem) {
            <p class="error" role="alert">{{ problem }}</p>
          }

          <div class="write-actions">
            <!--
              Write, not OK: the button that changes a plant should say what it does, and an
              explicit submit means a stray Enter in the field does nothing until it is pressed --
              which is what an operator expects from a form. Escape closes without writing.
            -->
            <button type="submit" [disabled]="writing()">
              {{ writing() ? 'Writing…' : 'Write' }}
            </button>
            <button type="button" class="ghost" (click)="cancelWrite()" [disabled]="writing()">
              Cancel
            </button>
          </div>
        </form>
      </div>
    }
  `,
  styles: `
    :host { display: block; }
    .row {
      display: grid;
      grid-template-columns: repeat(12, 1fr);
      gap: 12px;
      margin-bottom: 12px;
    }
    /* A cell is a layer on the page, so a soft shadow rather than an outlined box. */
    .cell {
      background: var(--surface);
      border-radius: var(--radius-lg);
      box-shadow: var(--shadow);
      padding: 13px 15px 15px;
      min-width: 0;
    }
    /* A grid row's cells share a height, tallest wins, so the row reads as one band. Without the
       flex column a tile's content sat at the top of a stretched cell and a trend beside an alarm
       list left the two halves of the screen visibly different heights — obvious at 1920. */
    .row > .cell { display: flex; flex-direction: column; }
    .cell > .chart { flex: 1; min-height: 120px; height: auto; }
    /* The reading row takes a share of the viewport rather than only what its text needs, so a
       screen built for a wall panel uses the panel: at 1920 the whole console occupied the top
       third of the glass and the rest was empty. Capped, because past a point a large empty tile
       is its own kind of unreadable. */
    .row > .cell:has(.reading) { min-height: clamp(120px, 13vh, 210px); }
    .label { margin: 0; font-size: var(--text-lg); font-weight: 600; letter-spacing: -0.01em; }
    .caption {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 8px;
      margin: 0 0 7px;
      font-size: 0.76rem;
      color: var(--text-muted);
    }
    .path { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    /* The reading is what the tile is for, so it is the largest thing in it and scales with the
       viewport: on a 1920 panel a fixed 1.7rem is a number read from two metres away, which was the
       first thing the large-screen look found on 2026-10-06. */
    .reading {
      margin: 0;
      font-size: clamp(1.55rem, 1.15rem + 1.1vw, 2.6rem);
      font-variant-numeric: tabular-nums;
      letter-spacing: -0.025em;
      color: var(--text);
      line-height: 1.1;
    }
    .at { margin: 5px 0 0; font-size: 0.72rem; color: var(--status-warn-ink); }
    /* A zero is dimmed rather than coloured: it is worth noticing, and it is not a fault. Colouring
       it would put a second meaning on the status colours, which is the one thing this palette
       reserves them for. */
    .reading.zero { color: var(--text-muted); }

    /* The state, written under the drawing. Not decoration: bad and stopped are different facts
       — "the plant says this machine is off" against "nothing is measuring it" — and a drawing that
       told them apart only by a shade of grey would be ADR-0003's failure where it is hardest to
       notice. A word is what a reader who does not know this palette has to go on. */
    .symbol-state {
      margin: 6px 0 0;
      font-size: var(--text-sm);
      text-transform: uppercase;
      letter-spacing: 0.07em;
      font-weight: 650;
      text-align: center;
      color: var(--text-muted);
    }
    .t-running { color: var(--status-good-ink); }
    .t-fault, .t-bad { color: var(--status-bad-ink); }
    .t-stale { color: var(--status-warn-ink); }

    /* The quality pill. Its colours are the semantic status tokens and nothing else, so "green"
       means Good and never merely "a nice colour" — see styles.css. */
    .quality {
      border-radius: var(--radius-pill);
      padding: 2px 8px;
      font-size: 0.68rem;
      font-weight: 650;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      background: var(--status-nodata-bg);
      color: var(--status-nodata-ink);
    }
    /* ADR-0026: the tag could be written. Deliberately not a button — the control that acts is
       .operate below, on the value component alone, and a marker that looked pressable would
       promise an action this element does not perform. */
    .writable {
      border-radius: var(--radius-pill);
      padding: 2px 8px;
      font-size: 0.68rem;
      font-weight: 650;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      background: var(--accent-soft);
      color: var(--text-body);
      border: 1px dashed var(--border-strong);
      cursor: default;
    }
    .quality.alone { display: inline-block; font-size: var(--text-md); padding: 4px 12px; }
    .q-Good { background: var(--status-good-bg); color: var(--status-good-ink); }
    /* Uncertain and Stale share a colour because they are the same message to an operator: the
       number is there and you should not lean on it. Bad is the one with nothing behind it. */
    .q-Uncertain { background: var(--status-warn-bg); color: var(--status-warn-ink); }
    .q-Stale { background: var(--status-warn-bg); color: var(--status-warn-ink); }
    .q-Bad { background: var(--status-bad-bg); color: var(--status-bad-ink); }
    .alarms { margin: 0; padding-left: 18px; }
    .alarms .limit { font-weight: 650; margin: 0 6px; }
    .empty, .unavailable { margin: 0; color: var(--text-muted); }
    .unavailable { font-style: italic; }
    .muted { margin: 0; color: var(--text-muted); }

    /* The write control. Understated on purpose: it sits beside a value an operator reads, and a
       control that looked like the most important thing on the screen would make an operating
       action look like the normal state of the screen. */
    .operate { margin-top: 9px; font-size: 0.76rem; }

    /* No backdrop-click to dismiss: it is the gesture most likely to be accidental, and this is the
       one dialog whose accidental dismissal leaves the operator unsure whether the write happened. */
    .write-backdrop {
      position: fixed;
      inset: 0;
      background: var(--scrim);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 20;
    }
    .write-dialog {
      background: var(--surface-raised);
      border-radius: var(--radius-lg);
      padding: 18px 20px;
      min-width: 22rem;
      max-width: 32rem;
      box-shadow: var(--shadow-lg);
    }
    .write-dialog h3 { margin: 0 0 10px; font-size: var(--text-md); }
    .write-current { margin: 0 0 12px; display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; }
    .write-dialog label { display: block; margin-bottom: 12px; font-size: var(--text-sm); color: var(--text-muted); }
    .write-dialog input, .write-dialog select { display: block; margin-top: 4px; width: 100%; }
    .write-actions { display: flex; gap: 8px; }
  `,
})
export class ScreenView {
  /** The screen to draw. */
  readonly screen = input.required<Screen>();

  /** Every tag this session has a value for, by tag id. */
  readonly snapshots = input.required<ReadonlyMap<string, TagSnapshot>>();

  /** Every standing alarm this session may see. */
  readonly alarms = input.required<readonly Alarm[]>();

  /**
   * History for the `trend` components on this screen, **by tag id**.
   *
   * Keyed by tag rather than by component because the history of a trend is the history of its tag
   * — two trend components bound to one tag are two views of one series — and because a component an
   * author has just added to a draft has an id the server has never seen, so a preview keyed by
   * component could only ever say "Reading…".
   *
   * A map rather than one array, because a screen may hold several trends about different tags. A
   * tag with no entry is still being read and says so, which is not the same as one whose history
   * came back empty.
   */
  readonly history = input<ReadonlyMap<string, HistorySample[]>>(new Map());

  protected historyFor(tagId: string | null): HistorySample[] | null {
    return tagId === null ? null : (this.history().get(tagId) ?? null);
  }

  /**
   * Whether a tag of this kind has a value the write dialog can collect.
   *
   * Numeric and boolean only. A **text** tag would need its own field, a **discrete** tag is a code
   * whose meaning lives in the device, and **none** is a tag with no value at all — and the API
   * refuses a value of the wrong kind in every one of those cases, so offering the control would
   * offer a write that cannot succeed. That is the same rule as decision 2, applied to the tag
   * rather than to the reader.
   */
  protected writableAsANumberOrAFlag(kind: string): boolean {
    return kind === 'numeric' || kind === 'boolean';
  }

  protected readonly rows = computed(() =>
    groupIntoRows(this.screen().components).map((row) => ({
      rowIndex: row.rowIndex,
      components: row.components.map((component) => ({
        component,
        resolved: resolveComponent(component, this.snapshots(), this.alarms(), this.screen().siteId),
      })),
    })),
  );

  /**
   * The write in progress, or null (ADR-0026).
   *
   * Held here rather than in the app so that **the same dialog serves the read view and the editor's
   * preview**, the same way one renderer serves both — a second dialog is a second set of rules about
   * when it closes and what it keeps.
   */
  protected readonly writeTarget = signal<Extract<ResolvedScreenComponent, { kind: 'value' }> | null>(null);

  /** What the operator has typed, as text. Parsed on submit so a typo is refused rather than coerced. */
  protected readonly writeDraft = signal('');

  /** True while the request is in flight, which disables the dialog (ADR-0026 §3). */
  protected readonly writing = signal(false);

  /** What the server said when a write failed. Kept so the dialog stays open with its value. */
  protected readonly writeProblem = signal<string | null>(null);

  /** A write the operator has confirmed. The parent performs it — this component calls no API. */
  readonly writeRequested = output<{ tagId: string; value: number | boolean }>();

  protected beginWrite(resolved: Extract<ResolvedScreenComponent, { kind: 'value' }>): void {
    this.writeTarget.set(resolved);
    this.writeDraft.set('');
    this.writeProblem.set(null);
    this.writing.set(false);
  }

  protected cancelWrite(): void {
    // Refused while a request is out, because the answer is coming either way and closing the dialog
    // would leave the operator with no way to hear it (ADR-0026 §3).
    if (this.writing()) {
      return;
    }

    this.writeTarget.set(null);
    this.writeProblem.set(null);
  }

  /**
   * Confirms the dialog. Returns without writing when the text is not a value of the tag's kind.
   *
   * Parsed here rather than coerced to a number, because `Number('')` is `0` and writing zero to a
   * plant because a field was left empty is exactly the class of defect ADR-0003 exists to prevent.
   */
  protected confirmWrite(): void {
    const target = this.writeTarget();

    if (!target || this.writing()) {
      return;
    }

    const raw = this.writeDraft().trim();

    if (target.valueKind === 'boolean') {
      // A boolean tag takes true or false, and the field is a picker rather than free text, so
      // anything else here is not a typo a person made.
      if (raw !== 'true' && raw !== 'false') {
        this.writeProblem.set('Choose true or false.');
        return;
      }

      this.writing.set(true);
      this.writeProblem.set(null);
      this.writeRequested.emit({ tagId: target.tagId, value: raw === 'true' });
      return;
    }

    if (raw === '' || !Number.isFinite(Number(raw))) {
      this.writeProblem.set('Enter a number.');
      return;
    }

    this.writing.set(true);
    this.writeProblem.set(null);
    this.writeRequested.emit({ tagId: target.tagId, value: Number(raw) });
  }

  /**
   * Called by the parent when the write has finished, successfully or not.
   *
   * A failure keeps the dialog and the value (ADR-0026 §5): the operator corrects a typo or presses
   * Write again, rather than re-typing a setpoint because the message closed the box.
   */
  finishWrite(problem: string | null): void {
    this.writing.set(false);

    if (problem === null) {
      this.writeTarget.set(null);
      this.writeDraft.set('');
      this.writeProblem.set(null);
      return;
    }

    this.writeProblem.set(problem);
  }
}

/**
 * A component's resolved state, for a caller that wants the answer without the template.
 *
 * Exported because the resolver is the part worth testing, and a test that reaches into a component
 * to find it is a test of Angular rather than of the decision.
 */
export type { ResolvedScreenComponent, ScreenComponent };
