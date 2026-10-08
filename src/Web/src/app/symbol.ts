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
      <!-- Intake and discharge, so every drawing reads as equipment in a process rather than a badge.
           Shared by all four: it is the line the plant is on, not part of any one machine. -->
      <line class="pipe" x1="2" y1="46" x2="22" y2="46" />
      <line class="pipe" x1="98" y1="46" x2="118" y2="46" />

      @switch (shape()) {
        @case ('valve') {
          <!--
            Two triangles meeting at a point: the P&ID valve, which is the one shape a plant reader
            already knows without being taught. Open leaves it hollow, closed fills it — the
            convention, and it also means the two states differ in INK as well as in colour, so they
            are told apart on a monochrome printout and by a reader who cannot see the difference
            between green and grey.
          -->
          @if (state() !== 'bad') {
            <polygon class="body vee" points="22,24 22,68 60,46" />
            <polygon class="body vee" [class.shut]="state() === 'closed'" points="98,24 98,68 60,46" />
            <line class="stem" x1="60" y1="46" x2="60" y2="16" />
            <line class="stem" x1="46" y1="16" x2="74" y2="16" />
          }
        }

        @case ('tank') {
          <!--
            A vessel, with the band it is in shown as a fill at a FIXED height per state — never a
            height that tracks the reading (ADR-0027 §5). The author declares where low ends and high
            begins; the drawing says which band, and a reader who cannot measure a height is not asked
            to.
          -->
          @if (state() !== 'bad') {
            <rect class="body" x="34" y="14" width="52" height="64" rx="8" />
            @if (fill() !== null) {
              <rect class="fill" x="36" [attr.y]="fill()!.y" width="48" [attr.height]="fill()!.height" rx="6" />
            }
            <!-- The band's own line, so the level reads even where the fill is pale. -->
            @if (fill() !== null) {
              <line class="surface" x1="36" [attr.y1]="fill()!.y" x2="84" [attr.y2]="fill()!.y" />
            }
          }
        }

        @default {
          <!-- A pump or a motor: the same body, told apart by what is inside it. -->
          <circle class="body" cx="60" cy="46" r="30" />

          @if (state() !== 'bad') {
            @if (shape() === 'motor') {
              <!--
                The letter, which is the electrical convention for a motor and the thing that stops a
                reader taking it for a pump. It does not turn: the ROTOR is what turns on a pump, and a
                spinning letter would be decoration.
              -->
              <text class="letter" x="60" y="46" text-anchor="middle" dominant-baseline="central">M</text>
              @if (animates()) {
                <circle class="whirl" cx="60" cy="46" r="21" />
              }
            } @else {
              <!--
                Four vanes on a rotor, plus a hub. Turning the ROTOR rather than the body is what makes
                the motion read at a glance: a rotating circle looks like nothing at all.

                **Not drawn at all when the reading is Bad**, and that is this drawing's whole statement
                about ADR-0003. Found by looking, 2026-10-07: the vanes sit on a plus and the cross
                below is a diagonal, so the two together drew an eight-pointed star — the crossed-out
                pump read as a BUSIER pump, not as a cancelled one, and the only thing still separating
                "nothing is measuring this" from "the plant says it is off" was a change of colour.
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
            }
          }
        }
      }

      <!--
        A Bad reading is crossed out, and with the machine's own parts gone above there is nothing left
        for the cross to be confused with. Heavier than any line in any drawing, for the same reason:
        it is not part of the equipment, it is the mark that cancels it.

        One cross for all four shapes — "nothing is measuring this" is the same statement whatever the
        equipment, and four different cancellations would make a reader learn four.
      -->
      @if (state() === 'bad') {
        <line class="cross" x1="38" y1="24" x2="82" y2="68" />
        <line class="cross" x1="82" y1="24" x2="38" y2="68" />
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
    /* A valve's two triangles. Hollow when open and filled when shut, which is the P&ID convention and
       also the only difference that survives a monochrome printout. */
    .vee { fill: var(--equipment-body); }
    .vee.shut { fill: var(--equipment-line); }
    .stem { stroke: var(--equipment-line); stroke-width: 2.5; stroke-linecap: round; }
    /* The motor's letter: the electrical convention, and what stops a reader taking it for a pump. */
    .letter {
      fill: var(--equipment-line);
      font-size: 30px;
      font-weight: 600;
      font-family: inherit;
    }
    /* A motor has no visible rotor, so its running state turns a ring instead of a part of the
       machine -- a dashed circle whose dashes travel. Same rule as the impeller: it turns only when
       running, and never at a speed that pretends to be a reading. */
    .whirl {
      fill: none;
      stroke: var(--status-good-ink);
      stroke-width: 2.5;
      stroke-dasharray: 10 8;
      animation: turn 1.6s linear infinite;
      transform-origin: 60px 46px;
    }
    /* The band a tank is in. Pale, because it is a fill rather than a line and a solid block would
       outweigh every other symbol on the screen. */
    .fill { fill: var(--equipment-line); opacity: 0.18; }
    .surface { stroke: var(--equipment-line); stroke-width: 2; }
    .hub { fill: var(--equipment-line); }
    /* Heavier than a vane (3) on purpose: the cross is not part of the machine, it is the mark that
       cancels it, and it is the only thing inside the body once the rotor is gone. */
    .cross { stroke: var(--status-bad-ink); stroke-width: 4.5; stroke-linecap: round; }

    /* Turning, and nothing else moves. A duration rather than a value: how fast a pump runs is not
       this build's to guess (ADR-0027 §5), so one speed is used for every running machine. */
    .rotor.turning { animation: turn 1.6s linear infinite; }
    @keyframes turn { to { transform: rotate(360deg); } }

    /* Someone who has asked their system not to animate things is asking for this too. The state is
       still drawn -- by the ink, the cross and the word -- so nothing is lost but the motion. */
    @media (prefers-reduced-motion: reduce) {
      .rotor.turning,
      .whirl { animation: none; }
    }

    /* The metal takes the state's ink. Green for running is the one colour a control room already
       reads without being taught, and it is the same green the quality pill uses, deliberately. */
    .s-running .body { stroke: var(--status-good-ink); }
    .s-running .vanes line, .s-running .hub, .s-running .pipe { stroke: var(--status-good-ink); }
    .s-fault .body { stroke: var(--status-bad-ink); }
    .s-fault .vanes line, .s-fault .hub, .s-fault .pipe { stroke: var(--status-bad-ink); }
    /* No rotor rule for this state, and its absence is the point: in bad the rotor is not in the
       drawing at all, so a rule for it would be a claim about something that is not there. */
    .s-bad .body { stroke: var(--status-bad-ink); stroke-dasharray: 5 4; }
    .s-bad .pipe { stroke: var(--status-bad-ink); }
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
  protected readonly animates = computed(
    () => (this.shape() === 'pump' || this.shape() === 'motor') && this.state() === 'running',
  );

  /**
   * Where a tank's fill sits, or null when there is nothing to fill it to.
   *
   * **Three fixed heights, one per band — never a height that tracks the reading** (ADR-0027 §5). The
   * author declares where low ends and high begins; this says which band, and a reader who cannot
   * measure a height is never asked to. A fill proportional to the number would be a continuing value
   * driving a picture, which is the thing that ADR refuses by name.
   *
   * `unknown` and `stale` draw no fill at all. A level nobody can vouch for must not be shown as a
   * quantity: an empty vessel would read as "it is empty", which is a claim about the plant.
   */
  protected readonly fill = computed<{ y: number; height: number } | null>(() => {
    // The vessel's inside, in the drawing's own units: y 14 to 78, inset by two.
    const top = 16;
    const bottom = 76;

    switch (this.state()) {
      case 'high':
        return { y: top, height: bottom - top };
      case 'normal':
        return { y: top + (bottom - top) * 0.45, height: (bottom - top) * 0.55 };
      case 'low':
        return { y: top + (bottom - top) * 0.78, height: (bottom - top) * 0.22 };
      default:
        return null;
    }
  });
}
