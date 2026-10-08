// The build settings that the served page's security policy depends on.
//
// **This file exists because of a defect that was correct in every source file and wrong in the
// product.** The Gateway serves a `script-src` made of `'self'` plus a hash per inline script
// (ADR-0031 §8). A hashed `script-src` **cannot** allow an inline event handler — `onload=`,
// `onclick=` — by specification: hashes do not apply to them without `'unsafe-hashes'`, and adding
// that keyword to let one attribute run would reopen the hole the hashes exist to close.
//
// Angular's critical-CSS inlining (Beasties) emits exactly such an attribute. It parks the real
// stylesheet at `media="print"` and swaps it back with `onload="this.media='all'"`. Under the policy
// the swap was blocked, the swap never happened, and **the entire stylesheet never applied** — the
// page was drawn from the inlined "critical" subset alone, which contained `:root` and not
// `[data-theme='dark']`, because nothing at build time carried that attribute. The dark theme was
// dead in the deployed product and perfect in `styles.css`.
//
// Seen in the browser on 2026-10-08 by switching the theme and watching nothing happen. No test could
// have seen it: every test here reads sources, and the defect lived in what the optimizer did to them.
// So the guard is placed on the setting itself, which is the thing that can regress.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const angular = JSON.parse(
  readFileSync(fileURLToPath(new URL('../angular.json', import.meta.url)), 'utf8'),
);

const production =
  angular.projects.ScadaDarboxWeb.architect.build.configurations.production;

test('the production build does not inline critical CSS', () => {
  // The whole point. `inlineCritical` defaults to TRUE, so this has to be written down explicitly --
  // leaving the setting out is the broken state, not the safe one.
  assert.equal(
    typeof production.optimization,
    'object',
    'the production build must spell out its optimization settings, because the default inlines critical CSS',
  );
  assert.equal(
    production.optimization.styles?.inlineCritical,
    false,
    'inlineCritical must be false: it emits an inline onload handler that a hashed script-src cannot allow, '
      + 'which leaves the real stylesheet parked at media="print" and never applied',
  );
});

test('turning it off did not quietly turn off minification with it', () => {
  // Spelling out `optimization` as an object replaces the `true` that was there, so every sub-setting
  // now has to be stated. Dropping minification would be a silent regression in the same edit -- a
  // bigger bundle on a plant network, and nothing that fails.
  assert.equal(production.optimization.styles?.minify, true, 'styles must still be minified');
  assert.equal(production.optimization.scripts, true, 'scripts must still be minified');
  assert.equal(production.optimization.fonts, true, 'fonts must still be optimized');
});

test('the output is still hashed, so an upgrade cannot be served a stale bundle', () => {
  // Unrelated to the policy and checked in the same breath, because it lives in the object that was
  // just edited and losing it would show up only as an upgrade that looks like it did nothing.
  assert.equal(production.outputHashing, 'all');
});
