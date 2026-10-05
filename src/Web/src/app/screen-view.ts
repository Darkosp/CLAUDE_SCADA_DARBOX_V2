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
  imports: [TrendChart, FormsModule],
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
                    <span class="path">{{ $any(cell.resolved).path }}</span>
                    @if ($any(cell.resolved).writable) {
                      <span class="writable" title="This tag can be written">writable</span>
                    }
                    <span class="quality" [class]="'q-' + $any(cell.resolved).quality">
                      {{ $any(cell.resolved).quality }}
                    </span>
                  </p>
                  <p class="reading">{{ $any(cell.resolved).text }}</p>
                  @if ($any(cell.resolved).sourceTimestampUtc; as at) {
                    <p class="at">at {{ at }}</p>
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
                @case ('trend') {
                  <p class="caption">
                    <span class="path">{{ $any(cell.resolved).path }}</span>
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
    .cell {
      background: #fff;
      border: 1px solid #e3e7ec;
      border-radius: 8px;
      padding: 12px 14px;
      min-width: 0;
    }
    .label { margin: 0; font-size: 1.05rem; }
    .caption {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      margin: 0 0 4px;
      font-size: 0.8rem;
      color: #5b6672;
    }
    .path { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .reading { margin: 0; font-size: 1.6rem; font-variant-numeric: tabular-nums; }
    .at { margin: 2px 0 0; font-size: 0.72rem; color: #8a949e; }
    .quality { border-radius: 999px; padding: 1px 8px; font-size: 0.72rem; background: #eef1f4; }
    /* ADR-0024 §9: marked, and deliberately not a button. Writing from a screen is the next slice,
       and a marker that looked pressable would promise something the build cannot do. */
    .writable {
      border-radius: 999px;
      padding: 1px 8px;
      font-size: 0.72rem;
      background: #eaeefb;
      color: #2f4a9c;
      border: 1px dashed #b9c4e6;
      cursor: default;
    }
    .quality.alone { display: inline-block; font-size: 0.95rem; padding: 4px 12px; }
    .q-Good { background: #e3f6e9; color: #1c6b3a; }
    .q-Uncertain { background: #fdf3dc; color: #8a6100; }
    .q-Stale { background: #fdf3dc; color: #8a6100; }
    .q-Bad { background: #fbe6e6; color: #99201f; }
    .alarms { margin: 0; padding-left: 18px; }
    .alarms .limit { font-weight: 600; margin: 0 6px; }
    .empty, .unavailable { margin: 0; color: #5b6672; }
    .unavailable { font-style: italic; }
    .muted { margin: 0; color: #5b6672; }

    /* The write control. Understated on purpose: it sits beside a value an operator reads, and a
       control that looked like the most important thing on the screen would make an operating
       action look like the normal state of the screen. */
    .operate { margin-top: 6px; font-size: 0.78rem; }

    /* No backdrop-click to dismiss: it is the gesture most likely to be accidental, and this is the
       one dialog whose accidental dismissal leaves the operator unsure whether the write happened. */
    .write-backdrop {
      position: fixed;
      inset: 0;
      background: rgba(20, 28, 38, 0.45);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 20;
    }
    .write-dialog {
      background: #fff;
      border-radius: 10px;
      padding: 18px 20px;
      min-width: 22rem;
      max-width: 32rem;
      box-shadow: 0 12px 40px rgba(20, 28, 38, 0.3);
    }
    .write-dialog h3 { margin: 0 0 10px; font-size: 1rem; }
    .write-current { margin: 0 0 12px; display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; }
    .write-dialog label { display: block; margin-bottom: 12px; font-size: 0.82rem; }
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
