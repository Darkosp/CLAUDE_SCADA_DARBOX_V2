// Plain JavaScript and Node's own test runner, like the other tests here.
//
// The editor's preview (ADR-0024's authoring slice). The preview is the same `app-screen` the read
// view uses, given the draft rather than the saved screen, so that there is no second renderer to
// drift. Almost all of what that means is already covered: `screen-resolution.test.mjs` tests what a
// component resolves to, and `screen-authoring.test.mjs` tests what the authoring operations do.
//
// What is NOT covered anywhere else is the one thing the preview depends on and could quietly lose:
// a component an author has just added carries `readable: true`, because `readable` is the server's
// answer about the *reader* and a component that has never been to the server has no answer yet. If
// that ever became `false`, or were fetched from somewhere, every component in the preview would read
// "Not available to you" -- and the author would be looking at a screen that says the opposite of what
// the operator will see. That is the failure this file exists to catch, and the control for it is that
// the flag still decides: a component with it false renders unreadable.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { newComponent, resolveComponent, toSaveComponent } from '../.node-test/screen.js';

/** A snapshot, as the stream leaves it. Only what the resolver reads is filled in. */
function snapshot(tagId, numeric, quality = 'Good') {
  return {
    tagId,
    path: 'Plant/Pump/Pressure',
    value: { kind: 'numeric', numeric },
    quality,
    sourceTimestampUtc: '2026-10-05T20:00:00.0000000+00:00',
    unitSymbol: 'bar',
  };
}

const SITE = 'site-1';

/** A value component bound to `tagId`, as the Add button makes one. */
function bound(tagId) {
  return newComponent('value', tagId, null);
}

test('a component an author just added resolves through the preview, because readable is true', () => {
  // The step the preview is for: add a value component, bind it to a tag this session has a value
  // for, and the preview shows that value rather than refusing it.
  const resolved = resolveComponent(
    bound('tag-1'),
    new Map([['tag-1', snapshot('tag-1', 4.2)]]),
    [],
    SITE,
  );

  assert.equal(resolved.kind, 'value');
  assert.equal(resolved.text, '4.20');
  assert.equal(resolved.quality, 'Good');
  // The unit is not part of the text; the view takes it off the snapshot beside it. Asserted so that
  // a future change folding it into `text` is a decision rather than a surprise.
  assert.equal(snapshot('tag-1', 4.2).unitSymbol, 'bar');
});

test('the control: with readable false the same component renders unreadable', () => {
  // Shows the flag is load-bearing rather than decoration. A server answer of false is what a tag the
  // reader may not see looks like, and the preview must show that too -- there is no path that lets an
  // author see a value the operator will not.
  const refused = { ...bound('tag-1'), readable: false };

  const resolved = resolveComponent(
    refused,
    new Map([['tag-1', snapshot('tag-1', 4.2)]]),
    [],
    SITE,
  );

  assert.equal(resolved.kind, 'unreadable');
});

test('readable is still not sent, so a preview cannot grant itself a binding', () => {
  // The other half of the same rule. The preview runs on `readable: true`, and the moment that value
  // left the client the server would be taking an author's word for what the author may see.
  const sent = toSaveComponent(bound('tag-1'));

  assert.equal(Object.hasOwn(sent, 'readable'), false);
  assert.deepEqual(Object.keys(sent).sort(), [
    'columnSpan', 'id', 'kind', 'position', 'rowIndex', 'states', 'symbol', 'tagId', 'title',
  ]);
});

test('a component the author has not bound yet says so rather than showing zero', () => {
  // What the preview shows mid-edit, before a tag is picked. ADR-0024's shape is that a component
  // cannot print a value with nowhere for its quality to go, and the same rule makes it unable to
  // print a value it does not have -- a count of zero would be a fabricated reading.
  const resolved = resolveComponent(bound(null), new Map(), [], SITE);

  assert.equal(resolved.kind, 'missing');
  assert.match(resolved.note, /names no tag/);
});

test('a bound tag with no reading yet is missing, not Bad and not zero', () => {
  // The state a freshly bound component is in for its first moment: the tag exists and is readable,
  // and nothing has arrived for it. It must not read as Bad, because Bad is a tag that HAS a reading
  // and it is not good -- and a dash or a zero there would be an invented value.
  const resolved = resolveComponent(bound('tag-1'), new Map(), [], SITE);

  assert.equal(resolved.kind, 'missing');
  assert.match(resolved.note, /No reading/);
});
