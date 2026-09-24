// Plain JavaScript and Node's own test runner, like the other tests here (see
// parse-number-field.test.mjs for why).
//
// ADR-0016 in the client. A pushing device has no scan interval, so the form sends none and the
// Gateway would refuse one; and a pushed tag that has never received anything reads "no data
// since" the moment listening began, so a device never set up reads differently from one that
// fell silent.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { noDataNote, pushes, scanIntervalToSend } from '../.node-test/models.js';

const drivers = [
  { key: 'modbus-tcp', pushing: false },
  { key: 'opc-ua', pushing: false },
  { key: 'mqtt', pushing: true },
];

test('a pushing driver is recognised, whatever the case typed', () => {
  assert.equal(pushes(drivers, 'mqtt'), true);
  assert.equal(pushes(drivers, ' MQTT '), true);
  assert.equal(pushes(drivers, 'modbus-tcp'), false);
  // Unknown is polled, as the Gateway treats it.
  assert.equal(pushes(drivers, 'something-else'), false);
});

test('a pushing device is sent no scan interval, even if the box still holds one', () => {
  assert.deepEqual(scanIntervalToSend(drivers, 'mqtt', 1000), { ok: true, value: null });
  assert.deepEqual(scanIntervalToSend(drivers, 'mqtt', ''), { ok: true, value: null });
});

test('a polled device still needs a positive scan interval', () => {
  assert.deepEqual(scanIntervalToSend(drivers, 'modbus-tcp', 500), { ok: true, value: 500 });
  assert.deepEqual(scanIntervalToSend(drivers, 'modbus-tcp', '250'), { ok: true, value: 250 });
  assert.equal(scanIntervalToSend(drivers, 'modbus-tcp', '').ok, false);
  assert.equal(scanIntervalToSend(drivers, 'modbus-tcp', 0).ok, false);
  assert.equal(scanIntervalToSend(drivers, 'modbus-tcp', 'fast').ok, false);
});

test('a tag that has never received anything says since when', () => {
  const note = noDataNote({ noDataSinceUtc: '2026-09-24T08:15:00Z' }, 'en-GB');

  assert.ok(note?.startsWith('No data since '), note);
  assert.match(note, /24 Sept?/);
});

test('any other tag gets no such note', () => {
  assert.equal(noDataNote({ noDataSinceUtc: null }), null);
  assert.equal(noDataNote({}), null);
});
