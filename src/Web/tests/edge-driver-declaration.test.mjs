// Plain JavaScript and Node's own test runner, like the other tests here (see
// parse-number-field.test.mjs for why).
//
// ADR-0019 §8 and ADR-0021 in the client. An edge declares which drivers its own build has; the
// Gateway refuses a device that edge cannot read, and the client says the same thing before the
// save is made. The one case that must never be muddled is an edge that has declared nothing: it is
// not a wrong edge, it is one nobody has heard from, and its device is accepted.
//
// §8 covers the assignment made after the declaration. ADR-0021 covers the one it cannot see: a
// device assigned before the edge's build lost a driver, which no save re-examines and which
// therefore reaches the client only because the edge reported it.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { declarationOf, edgeDriverNote, unreadableOf } from '../.node-test/models.js';

const edge = (declaredDriverKeys, name = 'edge-a', unreadableDevices = []) => ({
  id: 'e1',
  name,
  linkDeviceId: 'd1',
  deviceIds: [],
  declaredDriverKeys,
  driversDeclaredAtUtc: declaredDriverKeys === null ? null : '2026-10-02T09:30:00+00:00',
  unreadableDevices,
});

test('an edge that has not declared says so, and is not shown the Gateway’s own drivers', () => {
  const said = declarationOf(edge(null));

  assert.match(said, /has not declared its drivers yet/);
  assert.doesNotMatch(said, /modbus|opc-ua|mqtt/);
});

test('an edge that declared its drivers has them read back as its own statement', () => {
  assert.equal(declarationOf(edge(['modbus-tcp', 'opc-ua'])), "This edge has declared it has: 'modbus-tcp', 'opc-ua'.");
  assert.match(declarationOf(edge([])), /no drivers at all/);
});

test('a driver the edge has declared it does not have is refused before the save is made', () => {
  const note = edgeDriverNote(edge(['modbus-tcp']), 'opc-ua');

  assert.match(note, /Edge "edge-a" has declared it cannot read 'opc-ua'/);
  assert.match(note, /'modbus-tcp'/);
});

test('the declared keys are compared however they are cased, as the factories are looked up', () => {
  assert.equal(edgeDriverNote(edge(['Modbus-TCP']), 'modbus-tcp'), null);
});

test('a driver the edge has declared it has is not a note', () => {
  assert.equal(edgeDriverNote(edge(['modbus-tcp', 'opc-ua']), 'opc-ua'), null);
});

test('an edge that has declared nothing is not refused, and an edge not chosen says nothing', () => {
  // The ordinary order: a plant's devices are configured before its edge is switched on.
  assert.equal(edgeDriverNote(edge(null), 'opc-ua'), null);
  assert.equal(edgeDriverNote(null, 'opc-ua'), null);
});

test('an edge that declared no drivers at all refuses every device, by name', () => {
  const note = edgeDriverNote(edge([], 'empty-edge'), 'modbus-tcp');

  assert.match(note, /Edge "empty-edge" has declared it cannot read 'modbus-tcp'/);
  assert.match(note, /none at all/);
});

test('a device an edge is assigned and cannot read is said out loud', () => {
  // ADR-0021: the state no save re-examines, so this line is the only place an operator is told
  // why those tags are reading Bad.
  const said = unreadableOf(
    edge(['opc-ua'], 'plant-b', [
      { deviceId: 'd2', device: 'Pump Station PLC', driver: 'modbus-tcp', reportedByEdge: true },
    ]),
  );

  assert.match(said, /is assigned 'Pump Station PLC' \(needs 'modbus-tcp'\)/);
  assert.match(said, /tags will read Bad/);
});

test('several unreadable devices are counted and each is named', () => {
  const said = unreadableOf(
    edge(['opc-ua'], 'plant-b', [
      { deviceId: 'd2', device: 'Pump Station PLC', driver: 'modbus-tcp', reportedByEdge: true },
      { deviceId: null, device: 'Gone PLC', driver: 'modbus-tcp', reportedByEdge: true },
    ]),
  );

  assert.match(said, /2 devices it cannot read/);
  assert.match(said, /'Pump Station PLC' \(needs 'modbus-tcp'\)/);
  // A name the cloud can no longer resolve is still shown: it is what the edge said.
  assert.match(said, /'Gone PLC' \(needs 'modbus-tcp'\)/);
});

test('an edge with nothing unreadable says nothing at all', () => {
  // Not "nothing is wrong" as a sentence — an empty note would be noise on every healthy edge.
  assert.equal(unreadableOf(edge(['modbus-tcp'])), null);
  assert.equal(unreadableOf(edge(null)), null);
});
