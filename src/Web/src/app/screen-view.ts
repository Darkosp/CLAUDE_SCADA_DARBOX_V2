import { Component, computed, input } from '@angular/core';
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
  imports: [TrendChart],
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
                      <span class="writable" title="This tag could be written — from a screen, not yet">
                        writable
                      </span>
                    }
                    <span class="quality" [class]="'q-' + $any(cell.resolved).quality">
                      {{ $any(cell.resolved).quality }}
                    </span>
                  </p>
                  <p class="reading">{{ $any(cell.resolved).text }}</p>
                  @if ($any(cell.resolved).sourceTimestampUtc; as at) {
                    <p class="at">at {{ at }}</p>
                  }
                }
                @case ('status') {
                  <p class="caption">
                    @if ($any(cell.resolved).writable) {
                      <span class="writable" title="This tag could be written — from a screen, not yet">
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
                      <span class="writable" title="This tag could be written — from a screen, not yet">
                        writable
                      </span>
                    }
                  </p>
                  @if (historyFor(cell.component.id); as samples) {
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
   * History for the `trend` components on this screen, by component id.
   *
   * A map rather than one array, because a screen may hold several trend components and each is
   * about its own tag. A component with no entry is still being read and says so, which is not the
   * same as one whose history came back empty.
   */
  readonly history = input<ReadonlyMap<string, HistorySample[]>>(new Map());

  protected historyFor(componentId: string): HistorySample[] | null {
    return this.history().get(componentId) ?? null;
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
}

/**
 * A component's resolved state, for a caller that wants the answer without the template.
 *
 * Exported because the resolver is the part worth testing, and a test that reaches into a component
 * to find it is a test of Angular rather than of the decision.
 */
export type { ResolvedScreenComponent, ScreenComponent };
