import { Component, computed, input } from '@angular/core';
import { HistorySample } from './models';

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
        @for (segment of segments(); track $index) {
          <polyline class="line" [attr.points]="segment" />
        }
        <text class="tick" [attr.x]="4" [attr.y]="12">{{ high().toFixed(2) }}</text>
        <text class="tick" [attr.x]="4" [attr.y]="height - 4">{{ low().toFixed(2) }}</text>
      </svg>
      <p class="range">
        {{ samples().length }} samples · {{ low().toFixed(2) }}–{{ high().toFixed(2) }}
        {{ unitSymbol() }}
      </p>
    }
  `,
  styles: `
    :host { display: block; }
    .chart {
      width: 100%;
      height: 160px;
      background: #fff;
      border: 1px solid #e3e7ec;
      border-radius: 8px;
    }
    .line { fill: none; stroke: #2f6f4f; stroke-width: 1.5; vector-effect: non-scaling-stroke; }
    .tick { font-size: 10px; fill: #8a94a0; }
    .range { font-size: 0.75rem; color: #8a94a0; margin: 0.4rem 0 0; }
    .empty { color: #8a94a0; font-size: 0.85rem; margin: 0; }
  `,
})
export class TrendChart {
  readonly samples = input.required<HistorySample[]>();
  readonly unitSymbol = input<string>('');

  protected readonly width = 600;
  protected readonly height = 160;

  /**
   * Only Good numeric samples are plotted. A Bad sample carries no value at all, and
   * drawing a gap is honest where interpolating across it would invent a reading.
   */
  protected readonly points = computed<Point[]>(() => {
    const usable = this.usableSamples();

    if (usable.length < 2) {
      return [];
    }

    const times = usable.map((s) => new Date(s.sourceTimestampUtc).getTime());
    const values = usable.map((s) => s.value.numeric as number);

    const firstTime = times[0];
    const timeSpan = Math.max(times[times.length - 1] - firstTime, 1);
    const low = this.low();
    const span = Math.max(this.high() - low, Number.EPSILON);
    const padding = 8;

    return usable.map((_, index) => ({
      x: ((times[index] - firstTime) / timeSpan) * this.width,
      y:
        this.height -
        padding -
        ((values[index] - low) / span) * (this.height - padding * 2),
    }));
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
    const times = this.usableSamples().map((sample) =>
      new Date(sample.sourceTimestampUtc).getTime(),
    );

    if (times.length < 3) {
      return new Set();
    }

    const deltas = times.slice(1).map((time, index) => time - times[index]);
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
