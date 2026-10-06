import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * One equipment symbol, drawn in a state a mapping derived (ADR-0027).
 *
 * **The component draws; it does not decide.** Which state to be in was decided by `deriveSymbolState`
 * from the reading and the mapping, in Core's shape and tested without a browser — this file has no
 * branch about quality, about a comparison, or about what a rule means. That is the same split
 * `screen-view` already has with the resolver, and for the same reason: a decision made inside a
 * template is one that cannot be tested without a browser.
 *
 * **A state is drawn three ways at once**, because a picture of a machine on its own is ambiguous:
 *
 * 1. the metal takes a status ink, so a stopped pump is not merely un-animated but visibly settled;
 * 2. the impeller turns — and turns **only** when the state is `running` (ADR-0027 §5: animation comes
 *    from the state, and a continuing value never drives it, because a reader cannot measure a
 *    rotation and would read decoration as a reading);
 * 3. the state is written beneath, which is what makes the drawing legible to someone who does not
 *    know this project's colours.
 *
 * The third matters more than it looks. `bad` and `stopped` are different facts — one is "the plant
 * says this machine is off", the other is "nothing is measuring this machine" — and a drawing that
 * distinguished them only by a shade of grey would be ADR-0003's failure in the medium where it is
 * hardest to notice.
 */
@Component({
  selector: 'app-symbol',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg viewBox="0 0 120 96" class="symbol" [class]="'s-' + state()" role="img"
         [attr.aria-label]="shape() + ' is ' + state()">
      <!-- Intake and discharge, so the drawing reads as a machine in a process rather than a badge. -->
      <line class="pipe" x1="2" y1="46" x2="22" y2="46" />
      <line class="pipe" x1="98" y1="46" x2="118" y2="46" />

      <circle class="body" cx="60" cy="46" r="30" />

      <!--
        Four vanes on a rotor, plus a hub. Turning the ROTOR rather than the body is what makes the
        motion read at a glance: a rotating circle looks like nothing at all.
      -->
      <g class="rotor" [class.turning]="animates()" [style.transform-origin]="'60px 46px'">
        <g class="vanes">
          <line x1="60" y1="46" x2="60" y2="20" />
          <line x1="60" y1="46" x2="60" y2="72" />
          <line x1="60" y1="46" x2="34" y2="46" />
          <line x1="60" y1="46" x2="86" y2="46" />
        </g>
        <circle class="hub" cx="60" cy="46" r="5" />
      </g>

      <!-- A Bad reading is crossed out. A picture of a machine with no reading is the one thing this
           must not be mistaken for, so it is not drawn as a machine at all. -->
      @if (state() === 'bad') {
        <line class="cross" x1="40" y1="26" x2="80" y2="66" />
        <line class="cross" x1="80" y1="26" x2="40" y2="66" />
      }
    </svg>
  `,
  styles: `
    :host { display: block; }
    .symbol { width: 100%; height: 96px; display: block; }

    .pipe {
      stroke: var(--equipment-line);
      stroke-width: 3;
      stroke-linecap: round;
      opacity: 0.55;
    }
    .body {
      fill: var(--equipment-body);
      stroke: var(--equipment-line);
      stroke-width: 2.5;
    }
    .vanes line { stroke: var(--equipment-line); stroke-width: 3; stroke-linecap: round; }
    .hub { fill: var(--equipment-line); }
    .cross { stroke: var(--status-bad-ink); stroke-width: 3.5; stroke-linecap: round; }

    /* Turning, and nothing else moves. A duration rather than a value: how fast a pump runs is not
       this build's to guess (ADR-0027 §5), so one speed is used for every running machine. */
    .rotor.turning { animation: turn 1.6s linear infinite; }
    @keyframes turn { to { transform: rotate(360deg); } }

    /* Someone who has asked their system not to animate things is asking for this too. The state is
       still drawn -- by the ink, the cross and the word -- so nothing is lost but the motion. */
    @media (prefers-reduced-motion: reduce) {
      .rotor.turning { animation: none; }
    }

    /* The metal takes the state's ink. Green for running is the one colour a control room already
       reads without being taught, and it is the same green the quality pill uses, deliberately. */
    .s-running .body { stroke: var(--status-good-ink); }
    .s-running .vanes line, .s-running .hub, .s-running .pipe { stroke: var(--status-good-ink); }
    .s-fault .body { stroke: var(--status-bad-ink); }
    .s-fault .vanes line, .s-fault .hub, .s-fault .pipe { stroke: var(--status-bad-ink); }
    .s-bad .body { stroke: var(--status-bad-ink); stroke-dasharray: 5 4; }
    .s-bad .vanes line, .s-bad .hub, .s-bad .pipe { stroke: var(--status-bad-ink); }
    .s-stale .body { stroke: var(--status-warn-ink); }
    .s-stale .vanes line, .s-stale .hub, .s-stale .pipe { stroke: var(--status-warn-ink); }
    .s-unknown .body { stroke: var(--status-nodata-ink); stroke-dasharray: 5 4; }
    .s-unknown .vanes line, .s-unknown .hub, .s-unknown .pipe { stroke: var(--status-nodata-ink); }
    .s-stopped .body { stroke: var(--status-nodata-ink); }
    .s-stopped .vanes line, .s-stopped .hub, .s-stopped .pipe { stroke: var(--status-nodata-ink); }
  `,
})
export class SymbolView {
  /** Which drawing. One for now (ADR-0027 §6). */
  readonly shape = input.required<string>();

  /** The state `deriveSymbolState` resolved. Never null — the resolver always answers. */
  readonly state = input.required<string>();

  /**
   * Whether this state animates.
   *
   * Derived from the shape and the state rather than passed in, because whether a machine *turns* is a
   * property of the drawing and the state — not something an author chooses and not something a
   * mapping can get wrong. A pump in `running` turns; a pump in any other state does not, and there is
   * no state an author could name that would make a stopped pump spin.
   */
  protected readonly animates = computed(() => this.shape() === 'pump' && this.state() === 'running');
}
