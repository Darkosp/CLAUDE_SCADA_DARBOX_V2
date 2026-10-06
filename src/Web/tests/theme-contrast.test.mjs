// Plain JavaScript and Node's own test runner, like the other tests here.
//
// Contrast, for both themes.
//
// This exists because the numbers were wrong and nothing said so: on 2026-10-06 the light theme's
// faintest text measured **2.82:1 on the page background**, where WCAG AA asks for 4.5 at that size,
// and the "no reading" quality pill measured 4.1. Neither is visible from reading the stylesheet —
// a hex value looks the same whether or not it can be read — and neither breaks anything, which is
// what makes a colour defect worth a test where a broken layout is not.
//
// **The rules, and why they are these:** 4.5:1 for text, which is AA for body text and the floor
// this project takes for anything a person is expected to read. Not 3:1 (AA for large text): a
// console is read at a glance from a distance, which argues for *more* contrast than a document, not
// less. Status pills are checked as text because that is what they are — a word on a tinted
// background.
//
// **The values are duplicated here on purpose.** They are copied from `src/styles.css`, so a change
// to the palette fails this test until it is copied too. That is the point: it makes the change
// deliberate rather than incidental, which is the same trade the other client tests make.

import { test } from 'node:test';
import assert from 'node:assert/strict';

/** WCAG relative luminance. */
function luminance(hex) {
  const value = hex.replace('#', '');
  const channels = [0, 2, 4].map((at) => {
    const channel = Number.parseInt(value.slice(at, at + 2), 16) / 255;
    return channel <= 0.03928 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
  });

  return 0.2126 * channels[0] + 0.7152 * channels[1] + 0.0722 * channels[2];
}

function contrast(foreground, background) {
  const a = luminance(foreground);
  const b = luminance(background);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

/** The floor, in one place so a test cannot quietly use a different one. */
const AA = 4.5;

/**
 * Both palettes, as `src/styles.css` defines them.
 *
 * Each theme lists its own text-on-surface pair for the darkest and lightest background it can land
 * on, because contrast is a property of a pair rather than of a colour.
 */
const THEMES = {
  light: {
    page: '#f4f5f7',
    surface: '#ffffff',
    sunken: '#eef0f3',
    text: '#14181c',
    body: '#3c4650',
    muted: '#626c77',
    // The equipment symbol's two strokes. In the light theme the rotor is the darker of the two,
    // which is the intuitive direction: the part that moves is drawn in the near-black accent.
    line: '#3c4650',
    moving: '#14181c',
    status: {
      goodInk: '#1d6b39',
      goodBg: '#e3f3e8',
      warnInk: '#8a6100',
      warnBg: '#fdf2dc',
      badInk: '#9b2c25',
      badBg: '#fbe6e6',
      nodataInk: '#5b6672',
      nodataBg: '#eef0f3',
    },
  },
  dark: {
    page: '#14161a',
    surface: '#1e2126',
    sunken: '#1b1e23',
    text: '#f2f4f7',
    body: '#c8ced6',
    muted: '#8b939d',
    // **The rotor is LIGHTER than the outline here, which is the reverse of the light theme and is
    // deliberate.** A dark rotor on a dark card is the one part of the drawing that would vanish, and
    // the rotor is the part that says the machine is running.
    line: '#aab2bc',
    moving: '#e8ecf1',
    status: {
      goodInk: '#7fd6a0',
      goodBg: '#14301f',
      warnInk: '#e8bf6a',
      warnBg: '#33280d',
      badInk: '#f09a92',
      badBg: '#3a1c1a',
      nodataInk: '#a8b0ba',
      nodataBg: '#2a2f36',
    },
  },
};

for (const [name, theme] of Object.entries(THEMES)) {
  test(`${name}: every level of text is readable on every surface it can land on`, () => {
    // The three levels against each of the three surfaces, because text is not always on a card:
    // a heading sits on the page, a caption on a card, a muted line inside a sunken panel.
    for (const surface of ['page', 'surface', 'sunken']) {
      for (const level of ['text', 'body', 'muted']) {
        const ratio = contrast(theme[level], theme[surface]);

        assert.ok(
          ratio >= AA,
          `${name}: ${level} (${theme[level]}) on ${surface} (${theme[surface]}) is ${ratio.toFixed(2)}:1, below the ${AA}:1 floor`,
        );
      }
    }
  });

  test(`${name}: every status pill's ink is readable on its own background`, () => {
    // A pill is a word on a tinted background, so the same floor applies. This is the check that
    // caught the light theme's "no reading" pill at 4.1:1.
    for (const [kind, pair] of Object.entries({
      good: [theme.status.goodInk, theme.status.goodBg],
      warn: [theme.status.warnInk, theme.status.warnBg],
      bad: [theme.status.badInk, theme.status.badBg],
      nodata: [theme.status.nodataInk, theme.status.nodataBg],
    })) {
      const ratio = contrast(pair[0], pair[1]);

      assert.ok(
        ratio >= AA,
        `${name}: the ${kind} pill is ${ratio.toFixed(2)}:1, below the ${AA}:1 floor`,
      );
    }
  });

  test(`${name}: the quality colours are distinguishable from each other`, () => {
    // Not a WCAG rule, and a real one for this product: Good, Warn and Bad are three *different*
    // answers to "can I trust this number", and three tints a reader cannot tell apart would make the
    // pill decorative.
    //
    // **The first version of this test compared luminance and failed, and the failure was the test's
    // fault rather than the palette's.** Light's Stale and Bad are #8a6100 and #9b2c25 — a brown and
    // a red, obviously different to look at, and near-identical in luminance, which is exactly what a
    // well-chosen warning palette looks like: what separates them is HUE, not brightness. Comparing
    // brightness would have demanded that one of them be made paler or darker for no reason a reader
    // would thank anyone for.
    //
    // So distance is measured in a rough perceptual space — a weighted RGB distance — which is crude
    // and is enough for the question actually being asked: could these be mistaken for one another.
    const inks = {
      good: theme.status.goodInk,
      warn: theme.status.warnInk,
      bad: theme.status.badInk,
    };

    const distance = (left, right) => {
      const channel = (hex, at) => Number.parseInt(hex.replace('#', '').slice(at, at + 2), 16);
      // Weights from the usual perceptual approximation: the eye reads green most, blue least.
      return Math.sqrt(
        2 * (channel(left, 0) - channel(right, 0)) ** 2 +
          4 * (channel(left, 2) - channel(right, 2)) ** 2 +
          3 * (channel(left, 4) - channel(right, 4)) ** 2,
      );
    };

    for (const [left, right] of [
      ['good', 'warn'],
      ['good', 'bad'],
      ['warn', 'bad'],
    ]) {
      const apart = distance(inks[left], inks[right]);

      assert.ok(
        apart > 60,
        `${name}: the ${left} (${inks[left]}) and ${right} (${inks[right]}) inks are only ${apart.toFixed(0)} apart — a reader could not tell them apart`,
      );
    }
  });
}

test('the light theme is genuinely light and the dark theme genuinely dark', () => {
  // A sanity check on the two tables above, so that a copy-paste that swapped them would fail here
  // rather than passing every contrast assertion by accident.
  assert.ok(contrast('#ffffff', THEMES.light.page) < 1.2, 'the light page should be near white');
  assert.ok(contrast('#000000', THEMES.dark.page) < 1.5, 'the dark page should be near black');
});

test('an equipment symbol stands off the card behind it', () => {
  // WCAG 1.4.11, and a lower bar than text on purpose: a stroke is a shape, not a sentence. The pump
  // draws its outline, its vanes, its hub and its pipes in the line colour, and its rotor in the
  // moving colour — and in the dark theme the moving colour is LIGHTER than the line, because a dark
  // rotor on a dark card would be the one part of the drawing that vanished.
  //
  // The states are drawn with the status inks, so this checks the three that change: a running pump,
  // a stopped one, and a bad one. Those inks are already checked as text above; what is new is that
  // they are also used as STROKES, and a stroke has far less area in which to be seen.
  //
  // Measured live first, by tools/audit-contrast.mjs against the running client — 6.53:1 for a running
  // pump in the light theme, 7.37:1 for a stopped one in the dark. This pins the rest so a palette
  // change cannot quietly drop one of them below the bar.
  for (const [name, theme] of Object.entries(THEMES)) {
    const strokes = [
      ['the outline', theme.line],
      ['the rotor', theme.moving],
      ['running', theme.status.goodInk],
      ['stopped', theme.status.nodataInk],
      ['bad', theme.status.badInk],
    ];

    for (const [what, colour] of strokes) {
      const ratio = contrast(colour, theme.surface);

      assert.ok(
        ratio >= 3,
        name +
          ': ' +
          what +
          ' (' +
          colour +
          ') is only ' +
          ratio.toFixed(2) +
          ':1 on the card (' +
          theme.surface +
          '), so a reader could not make out the drawing',
      );
    }
  }
});
