import { Component, computed, input } from '@angular/core';
import {
  TREND_POINTS,
  TrendSeries,
  bucketColumns,
  bucketGaps,
  readingsIn,
  trendAxis,
} from './models';

interface Point {
  x: number;
  y: number;
}

/**
 * A basic historical trend for one numeric tag.
 *
 * Drawn as inline SVG rather than with a charting library: ADR-0006 names no charting
 * dependency, and "basic trend chart" is what this phase scopes. Anything richer —
 * zoom, pan, multiple series — is a library decision that needs its own ADR.
 *
 * **It draws buckets, not readings, and that is the point of ADR-0029.** The reduction from a
 * window's readings to what fits across the chart used to happen here — every reading crossed the
 * wire first — and it happens in the query now. What this component still owns is the part only it
 * can know: where across the chart each bucket belongs, and where the line must break.
 */
@Component({
  selector: 'app-trend-chart',
  template: `
    @if (points().length < 2) {
      <p class="empty">Not enough history yet.</p>
    } @else {
      <svg [attr.viewBox]="'0 0 ' + width + ' ' + height" class="chart" role="img"
           [attr.aria-label]="'Trend for ' + unitSymbol()">
        <!-- Behind the line: what each column's readings actually reached. -->
        @for (bar of envelope(); track bar.x) {
          <line class="envelope" [attr.x1]="bar.x" [attr.y1]="bar.top"
                [attr.x2]="bar.x" [attr.y2]="bar.bottom" />
        }
        @for (segment of segments(); track $index) {
          <polyline class="line" [attr.points]="segment" />
        }
        <text class="tick" [attr.x]="4" [attr.y]="12">{{ high().toFixed(2) }}</text>
        <text class="tick" [attr.x]="4" [attr.y]="height - 4">{{ low().toFixed(2) }}</text>
      </svg>
      <!--
        **The period, under the chart, always.** A curve with no stated period is a curve nobody can
        reason about: before this, the same picture could be a quarter of an hour or a week and the
        reader had no way to tell. The ends are the window that was ASKED FOR, not the first and last
        reading held, so an outage at either end reads as empty chart rather than disappearing.

        And **how much is behind the curve**: the readings, then the points they are drawn as. Both,
        because a reader who is told only the points cannot tell a quiet window from a reduced one —
        which is the same reason nothing here is capped without saying so (ADR-0029 §3).
      -->
      <p class="axis">
        <span>{{ axis().start }}</span>
        <span class="count">{{ readings() }} readings · {{ points().length }} points · {{ low().toFixed(2) }}–{{ high().toFixed(2) }} {{ unitSymbol() }}</span>
        <span>{{ axis().end }}</span>
      </p>
    }
  `,
  styles: `
    :host { display: block; }
    .chart {
      width: 100%;
      height: 160px;
      background: var(--surface);
      border: 1px solid var(--border);
      border-radius: var(--radius);
    }
    /* A neutral ink rather than a hue: the accent and the status colours both mean something here,
       and a third meaning for "this is a line" would only compete with them. */
    .line { fill: none; stroke: var(--chart-line); stroke-width: 1.5; vector-effect: non-scaling-stroke; }
    /* The same ink as the line, thinned: it is the same measurement, shown as a reach rather than a
       value, and a second colour would read as a second quantity. */
    .envelope { stroke: var(--chart-line); stroke-width: 1; opacity: 0.35; }
    .tick { font-size: 10px; fill: var(--text-muted); }
    .axis {
      display: flex;
      justify-content: space-between;
      gap: 8px;
      font-size: var(--text-sm);
      color: var(--text-muted);
      margin: 0.4rem 0 0;
    }
    /* The ends are the axis and belong at the edges; what the line holds is a caption and belongs
       between them, so the three never read as one sentence. */
    .axis .count { text-align: center; }
    .empty { color: var(--text-muted); font-size: var(--text-base); margin: 0; }
  `,
})
export class TrendChart {
  /**
   * The window as the server reduced it, with the width it reduced it to.
   *
   * One input rather than a list and a width, because those two have to agree: a chart handed
   * buckets and a width from two places could place every column at the wrong x and look fine.
   */
  readonly series = input.required<TrendSeries>();

  readonly unitSymbol = input<string>('');

  /**
   * The window this trend was asked for — the chart's x-axis, end to end.
   *
   * Required, and taken from the caller rather than derived from the data, because the data cannot
   * tell a window apart from what happens to be in it: a device offline for the first ten minutes
   * of a quarter-hour leaves five minutes of buckets, and drawn against themselves they fill the
   * chart as though nothing had been missing.
   */
  readonly from = input.required<Date>();
  readonly to = input.required<Date>();

  /** Both ends of the axis, as a reader can place them. */
  protected readonly axis = computed(() => trendAxis(this.from(), this.to()));

  protected readonly width = TREND_POINTS;
  protected readonly height = 160;

  /**
   * The buckets placed across the chart, each keeping the extremes of what was measured in it.
   *
   * A bucket the server read nothing plottable in is dropped here rather than drawn: it is a hole
   * with a count, and drawing it as a value would be the fabrication ADR-0003 refuses.
   */
  protected readonly columns = computed(() =>
    bucketColumns(this.series(), this.from().getTime(), this.to().getTime(), this.width),
  );

  /** How many readings the curve is drawn from — what the caption means by "readings" (ADR-0029 §4). */
  protected readonly readings = computed(() => readingsIn(this.series()));

  protected readonly points = computed<Point[]>(() => {
    const columns = this.columns();

    if (columns.length < 2) {
      return [];
    }

    const low = this.low();
    const span = Math.max(this.high() - low, Number.EPSILON);
    const padding = 8;
    const y = (value: number) =>
      this.height - padding - ((value - low) / span) * (this.height - padding * 2);

    // The midpoint of each bucket's envelope carries the line; the envelope itself is drawn behind
    // it, so neither hides the other.
    return columns.map((column) => ({ x: column.x, y: y((column.low + column.high) / 2) }));
  });

  /**
   * The envelope: for each column, the vertical reach of what was measured in it.
   *
   * Drawn only where a column actually spans a range — on a short trend a bucket may hold one
   * reading, so there is nothing to draw and the chart is the line alone.
   */
  protected readonly envelope = computed(() => {
    const low = this.low();
    const span = Math.max(this.high() - low, Number.EPSILON);
    const padding = 8;
    const y = (value: number) =>
      this.height - padding - ((value - low) / span) * (this.height - padding * 2);

    return this.columns()
      .filter((column) => column.high > column.low)
      .map((column) => ({ x: column.x, top: y(column.high), bottom: y(column.low) }));
  });

  /**
   * The line, split wherever history has a hole in it.
   *
   * A single polyline across a gap draws a straight ramp between the last reading before
   * an outage and the first one after — a line no device ever produced. Breaking the
   * stroke shows the absence instead, which is the same reason a Bad reading carries no
   * value rather than a substituted one.
   *
   * **The hole is found by a missing bucket** (ADR-0029 §6), which is exact. It used to be inferred
   * from the distances between columns against four times their median, and that rule could not see
   * a gap narrower than about four columns — over an hour of nothing at a seven-day resolution.
   */
  protected readonly segments = computed<string[]>(() => {
    const points = this.points();
    const gaps = bucketGaps(this.columns());

    const result: string[] = [];
    let current: Point[] = [];

    points.forEach((point, index) => {
      current.push(point);

      if (gaps.has(index)) {
        result.push(TrendChart.toPolyline(current));
        current = [];
      }
    });

    result.push(TrendChart.toPolyline(current));
    return result.filter((segment) => segment.length > 0);
  });

  private static toPolyline(points: Point[]): string {
    // A lone point has no line to draw; two are the minimum for a segment.
    return points.length < 2
      ? ''
      : points.map((point) => `${point.x.toFixed(1)},${point.y.toFixed(1)}`).join(' ');
  }

  protected readonly low = computed(() => {
    const columns = this.columns();
    return columns.length === 0 ? 0 : Math.min(...columns.map((column) => column.low));
  });

  protected readonly high = computed(() => {
    const columns = this.columns();
    return columns.length === 0 ? 0 : Math.max(...columns.map((column) => column.high));
  });
}
