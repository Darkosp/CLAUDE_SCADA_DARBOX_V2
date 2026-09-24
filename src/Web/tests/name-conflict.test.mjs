// Plain JavaScript and Node's own test runner, like the other tests here (see
// parse-number-field.test.mjs for why).
//
// ADR-0015. The Gateway refuses a name already taken within its parent with 409, and the
// client shows that against the name field rather than as a bare failure. Save is also
// disabled while a request is in flight — a courtesy against a double click, not the
// guarantee: the database index is the guarantee.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { SingleFlight, nameConflictMessage } from '../.node-test/models.js';

test('a 409 is a name already taken, and carries the reason to show against the field', () => {
  const refusal = { status: 409, message: "A device named 'Booster' already exists in the same folder." };
  assert.equal(nameConflictMessage(refusal), refusal.message);
});

test('any other failure is not a name clash, and stays in the general error line', () => {
  assert.equal(nameConflictMessage({ status: 400, message: 'Scan interval must be positive.' }), null);
  assert.equal(nameConflictMessage({ status: 500, message: 'Internal error' }), null);
  assert.equal(nameConflictMessage(new Error('Failed to fetch')), null);
  assert.equal(nameConflictMessage(null), null);
});

test('a second save while the first is in flight is not sent', async () => {
  const states = [];
  const flight = new SingleFlight((busy) => states.push(busy));
  let sent = 0;
  let answer;
  const reply = new Promise((resolve) => (answer = resolve));

  const first = flight.run(async () => {
    sent++;
    await reply;
  });
  const second = await flight.run(async () => {
    sent++;
  });

  assert.equal(second, false);
  assert.equal(flight.busy, true);

  answer();
  assert.equal(await first, true);
  assert.equal(sent, 1);
  assert.deepEqual(states, [true, false]);
});

test('once the reply is in, and even after a failure, the next save goes through', async () => {
  const flight = new SingleFlight();

  await assert.rejects(flight.run(async () => {
    throw new Error('409');
  }));
  assert.equal(flight.busy, false);

  let sent = 0;
  assert.equal(await flight.run(async () => { sent++; }), true);
  assert.equal(sent, 1);
});
