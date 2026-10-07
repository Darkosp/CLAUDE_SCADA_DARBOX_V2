import { Component, computed, input } from '@angular/core';
import { HistorySample, plotAcross, trendAxis, trendColumns } from './models';

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
      -->
      <p class="axis">
        <span>{{ axis().start }}</span>
        <span class="count">{{ samples().length }} samples · {{ low().toFixed(2) }}–{{ high().toFixed(2) }} {{ unitSymbol() }}</span>
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
  readonly samples = input.required<HistorySample[]>();
  readonly unitSymbol = input<string>('');

  /**
   * The window this trend was asked for — the chart's x-axis, end to end.
   *
   * Required, and taken from the caller rather than derived from the samples, because the samples
   * cannot tell a window apart from what happens to be in it: a device offline for the first ten
   * minutes of a quarter-hour leaves five minutes of readings, and drawn against themselves they
   * fill the chart as though nothing had been missing.
   */
  readonly from = input.required<Date>();
  readonly to = input.required<Date>();

  /** Both ends of the axis, as a reader can place them. */
  protected readonly axis = computed(() => trendAxis(this.from(), this.to()));

  protected readonly width = 600;
  protected readonly height = 160;

  /**
   * Only Good numeric samples are plotted. A Bad sample carries no value at all, and
   * drawing a gap is honest where interpolating across it would invent a reading.
   */
  /**
   * The readings reduced to one column per pixel, keeping each column's extremes.
   *
   * Below one reading per pixel this changes nothing, so a short trend is drawn exactly as it was
   * before this existed. Above it, `trendColumns` is what stops eight hours of a tag scanned every
   * second from arriving as a solid block of ink — and it keeps the envelope rather than an average,
   * so the spike that made somebody open the trend is still there.
   */
  protected readonly columns = computed(() =>
    trendColumns(
      this.usableSamples().map((sample) => ({
        time: new Date(sample.sourceTimestampUtc).getTime(),
        value: sample.value.numeric as number,
      })),
      this.from().getTime(),
      this.to().getTime(),
      this.width,
    ),
  );

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

    // The midpoint of each column's envelope carries the line; the envelope itself is drawn behind
    // it, so neither hides the other.
    return columns.map((column) => ({ x: column.x, y: y((column.low + column.high) / 2) }));
  });

  /**
   * The envelope: for each column, the vertical reach of what was measured in it.
   *
   * Drawn only where a column actually spans a range — on a short trend every column holds one
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
   */
  protected readonly segments = computed<string[]>(() => {
    const points = this.points();
    const gaps = this.gapAfterIndex();

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

  /**
   * Indexes after which the next sample is far enough away to count as an outage rather
   * than the normal scan interval. The threshold is derived from the data itself, so it
   * holds for a tag scanned once a second and one scanned once a minute alike.
   */
  private gapAfterIndex(): Set<number> {
    // **Over the COLUMNS, not the samples.** `points()` now holds one point per column, and an index
    // into the readings would split the line at places that no longer correspond to anything — a
    // defect introduced and caught in the same sitting, and the same shape as every other one this
    // project has found: two halves each correct, and the path between them left behind.
    //
    // Each column knows when its first and last reading were taken, so the distance between two
    // columns is the distance from the last reading of one to the first of the next: on a short
    // trend, where a column holds a single reading, that is exactly what it used to be.
    const columns = this.columns();

    if (columns.length < 3) {
      return new Set();
    }

    const deltas = columns
      .slice(1)
      .map((column, index) => column.firstTime - columns[index].lastTime);
    const sorted = [...deltas].sort((left, right) => left - right);
    const median = sorted[Math.floor(sorted.length / 2)];
    const threshold = Math.max(median * 4, 5_000);

    const gaps = new Set<number>();
    deltas.forEach((delta, index) => {
      if (delta > threshold) {
        gaps.add(index);
      }
    });

    return gaps;
  }

  protected readonly low = computed(() => {
    const values = this.numericValues();
    return values.length === 0 ? 0 : Math.min(...values);
  });

  protected readonly high = computed(() => {
    const values = this.numericValues();
    return values.length === 0 ? 0 : Math.max(...values);
  });

  private numericValues(): number[] {
    return this.usableSamples().map((sample) => sample.value.numeric as number);
  }

  /**
   * Samples that can be plotted at all: Good quality, with a numeric value. A Bad sample
   * carries no value, so there is nothing to place on the axis.
   */
  private usableSamples(): HistorySample[] {
    return this.samples().filter(
      (sample) => sample.quality === 'Good' && typeof sample.value.numeric === 'number',
    );
  }
}
