import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
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
      <svg #chart [attr.viewBox]="'0 0 ' + width() + ' ' + height()" class="chart" role="img"
           [attr.aria-label]="'Trend for ' + unitSymbol()">
        <!-- Behind the line: what each column's readings actually reached. -->
        @for (bar of envelope(); track bar.x) {
          <line class="envelope" [attr.x1]="bar.x" [attr.y1]="bar.top"
                [attr.x2]="bar.x" [attr.y2]="bar.bottom" />
        }
        @for (segment of segments(); track $index) {
          <polyline class="line" [attr.points]="segment" />
        }
        <text class="tick" [attr.x]="4" [attr.y]="plot().top - 2">{{ high().toFixed(2) }}</text>
        <text class="tick" [attr.x]="4" [attr.y]="height() - 6">{{ low().toFixed(2) }}</text>
      </svg>
      <!--
        **The period, under the chart, always.** A curve with no stated period is a curve nobody can
        reason about: before this, the same picture could be a quarter of an hour or a week and the
        reader had no way to tell. The ends are the window that was ASKED FOR, not the first and last
        reading held, so an outage at either end reads as empty chart rather than disappearing.

        **And it is set to the plot's own edges, not the card's.** The drawing is inset by a gutter for
        the value labels above, so a row of dates sitting flush with the card would name a time
        seventy pixels away from the point it belongs to — which is the defect this chart was walked
        for, an order of magnitude smaller. Both come from the same place, so they cannot drift.

        And **how much is behind the curve**: the readings, then the points they are drawn as. Both,
        because a reader who is told only the points cannot tell a quiet window from a reduced one —
        which is the same reason nothing here is capped without saying so (ADR-0029 §3).
      -->
      <p class="axis" [style.paddingLeft.px]="plot().left" [style.paddingRight.px]="plot().right">
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

  /**
   * The size the chart is **drawn at**, in its own coordinates — measured from the element it is in.
   *
   * **A fixed coordinate system in a responsive box is a lie about time, and this is the walk of
   * 2026-10-07 finding it.** The viewBox was the constant `0 0 600 160` inside a chart element that
   * filled its card, and the default `preserveAspectRatio` scales such a drawing uniformly to fit the
   * *shorter* side and centres it. On a 1920 panel that card is ~1480 px wide, so the curve was drawn
   * in a **607 px strip in the middle** while the axis beneath it ran the full width: every reading sat
   * at the wrong time by the width of the empty band — about seven hours on a day-long window — and the
   * two value ticks floated in the middle of the chart rather than at its left edge.
   *
   * No test could see it: the arithmetic was right, the buckets were right, and only the *rendered*
   * box disagreed with the coordinate system. Measured facts, from the running client: svg box
   * 1482×162, viewBox 600×160, first drawn point at x=846 of 1887.
   *
   * So the coordinate system is the element. Until the first measurement the fallback is
   * `TREND_POINTS`, which is one point per pixel at the width this chart used to be.
   */
  protected readonly width = signal(TREND_POINTS);
  protected readonly height = signal(160);

  private readonly destroyRef = inject(DestroyRef);
  private observer: ResizeObserver | null = null;

  /**
   * The drawing element itself, which **is not there on the first render and that cost this fix one
   * attempt.** Empty history draws the words "Not enough history yet" instead of a chart, so a
   * measurement taken after the component's first render found no SVG at all, returned, and never
   * looked again — the viewBox stayed the constant and the defect stayed with it. The element arrives
   * when the history does, so this is bound to it rather than to a moment.
   */
  private readonly chart = viewChild<ElementRef<SVGSVGElement>>('chart');

  constructor() {
    effect(() => {
      const svg = this.chart()?.nativeElement;

      if (svg === undefined) {
        return;
      }

      this.observer ??= new ResizeObserver(() => this.measure(svg));
      this.observer.observe(svg);

      // Re-measured rather than assumed, because the card moves: the pane is resized by the window, by
      // the tree beside it, and by a screen's own column count.
      this.measure(svg);
    });

    this.destroyRef.onDestroy(() => this.observer?.disconnect());
  }

  /** Writes the element's content box into the coordinate system the chart draws in. */
  private measure(svg: SVGSVGElement): void {
    const box = svg.getBoundingClientRect();
    const style = getComputedStyle(svg);
    const border = (side: 'borderLeftWidth' | 'borderRightWidth' | 'borderTopWidth' | 'borderBottomWidth') =>
      Number.parseFloat(style[side]) || 0;

    // The SVG's *viewport* is its content box, not its border box, and the two differ by the border
    // this chart draws. Measured off by a pixel on either side, `preserveAspectRatio` would letterbox
    // the drawing again — the same defect, two orders of magnitude smaller.
    const width = Math.round(box.width - border('borderLeftWidth') - border('borderRightWidth'));
    const height = Math.round(box.height - border('borderTopWidth') - border('borderBottomWidth'));

    if (width > 0 && height > 0 && (width !== this.width() || height !== this.height())) {
      this.width.set(width);
      this.height.set(height);
    }
  }

  /** Both ends of the axis, as a reader can place them. */
  protected readonly axis = computed(() => trendAxis(this.from(), this.to()));

  /**
   * Where the data is drawn inside the chart, and the room the labels need around it.
   *
   * **Found by the walk of 2026-10-07, in its second pass**: the two value labels were drawn *inside*
   * the plot at its left edge, and the reading there is by definition the lowest one — so `3.80` sat
   * on top of the curve it was labelling. Crowded rather than wrong, and the first pass at 1920 had
   * them floating in the middle of the card, which is where they should never have been either.
   *
   * So the plot is inset: a gutter on the left for the labels, and enough above and below that the
   * line can reach neither of them. **The axis row under the chart is inset by the same numbers** —
   * a row of dates flush with the card while the drawing starts seventy pixels in would name times
   * that belong to points further right, which is the defect this chart was walked for at all.
   *
   * The gutter is dropped on a card too narrow to hold both a label and a curve worth reading; a
   * trend in a tile is the case that has to keep working.
   */
  protected readonly plot = computed(() => {
    const width = this.width();
    const height = this.height();
    const gutter = width >= 360 ? 46 : 0;

    return {
      left: gutter,
      right: gutter > 0 ? 6 : 0,
      top: 14,
      bottom: 22,
      width: Math.max(width - gutter - (gutter > 0 ? 6 : 0), 1),
      height: Math.max(height - 14 - 22, 1),
    };
  });

  /**
   * The buckets placed across the plot, each keeping the extremes of what was measured in it.
   *
   * A bucket the server read nothing plottable in is dropped here rather than drawn: it is a hole
   * with a count, and drawing it as a value would be the fabrication ADR-0003 refuses.
   */
  protected readonly columns = computed(() => {
    const plot = this.plot();

    return bucketColumns(this.series(), this.from().getTime(), this.to().getTime(), plot.width)
      .map((column) => ({ ...column, x: column.x + plot.left }));
  });

  /** How many readings the curve is drawn from — what the caption means by "readings" (ADR-0029 §4). */
  protected readonly readings = computed(() => readingsIn(this.series()));

  /** A reading's value as a y inside the plot: the lowest sits on the plot's floor, the highest on its roof. */
  private toY(value: number): number {
    const plot = this.plot();
    const low = this.low();

    return plot.top + plot.height - ((value - low) / Math.max(this.high() - low, Number.EPSILON)) * plot.height;
  }

  protected readonly points = computed<Point[]>(() => {
    const columns = this.columns();

    if (columns.length < 2) {
      return [];
    }

    // The midpoint of each bucket's envelope carries the line; the envelope itself is drawn behind
    // it, so neither hides the other.
    return columns.map((column) => ({
      x: column.x,
      y: this.toY((column.low + column.high) / 2),
    }));
  });

  /**
   * The envelope: for each column, the vertical reach of what was measured in it.
   *
   * Drawn only where a column actually spans a range — on a short trend a bucket may hold one
   * reading, so there is nothing to draw and the chart is the line alone.
   */
  protected readonly envelope = computed(() =>
    this.columns()
      .filter((column) => column.high > column.low)
      .map((column) => ({ x: column.x, top: this.toY(column.high), bottom: this.toY(column.low) })),
  );

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
