// A pushing source's own journal entries (ADR-0017): a loss it reported and a clock that
// disagrees with the Gateway's. Plain JavaScript against the compiled models.js, as the others.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { describeSourceEvent, isEngineEvent } from '../.node-test/models.js';

test('a loss says how many samples the source dropped', () => {
  assert.equal(
    describeSourceEvent({ type: 'SamplesLost', lostSamples: 1200, clockSkewSeconds: null }),
    '1200 samples dropped at the source',
  );
  assert.equal(
    describeSourceEvent({ type: 'SamplesLost', lostSamples: 1, clockSkewSeconds: null }),
    '1 sample dropped at the source',
  );
});

test('a clock skew says how far and which way', () => {
  assert.equal(
    describeSourceEvent({ type: 'SourceClockSkew', lostSamples: null, clockSkewSeconds: 600.4 }),
    "source clock 10 min ahead of the Gateway's",
  );
  assert.equal(
    describeSourceEvent({ type: 'SourceClockSkew', lostSamples: null, clockSkewSeconds: -45 }),
    "source clock 45 s behind the Gateway's",
  );
  // A clock reset to its epoch is hours out, not thousands of minutes.
  assert.equal(
    describeSourceEvent({ type: 'SourceClockSkew', lostSamples: null, clockSkewSeconds: -9000 }),
    "source clock 2.5 h behind the Gateway's",
  );
});

test('every other entry has nothing to add here', () => {
  assert.equal(describeSourceEvent({ type: 'Raised', lostSamples: null, clockSkewSeconds: null }), null);
});

test('a source event is not the engine speaking: it names a Site', () => {
  assert.equal(isEngineEvent({ occurrenceId: null, siteId: null }), true);
  assert.equal(isEngineEvent({ occurrenceId: null, siteId: 'skopje' }), false);
  assert.equal(isEngineEvent({ occurrenceId: 'o', siteId: 'skopje' }), false);
});
