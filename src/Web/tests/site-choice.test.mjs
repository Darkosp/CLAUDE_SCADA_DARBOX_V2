// Plain JavaScript and Node's own test runner, like the other tests here (see
// parse-number-field.test.mjs for why).
//
// Two findings from walking the Phase 6 gate by hand. The client opened on a Site with no
// devices — the seeded second Site holds only an empty folder — which reads as "nothing
// works". And the alarm summary lists every Site the reader may see without saying which
// Site each alarm is on.

import { test } from 'node:test';
import assert from 'node:assert/strict';

import { deviceCount, siteName, siteToOpen } from '../.node-test/models.js';

const device = (id) => ({ id, name: id, driverKey: 'modbus-tcp', tags: [] });
const folder = (id, folders = [], devices = []) => ({ id, name: id, parentFolderId: null, folders, devices });

const bitola = { id: 'bitola', name: 'Bitola', timeZoneId: 'Europe/Skopje' };
const skopje = { id: 'skopje', name: 'Skopje', timeZoneId: 'Europe/Skopje' };

test('devices are counted at any depth of folders', () => {
  assert.equal(deviceCount({ folders: [folder('water-works')], devices: [] }), 0);
  assert.equal(deviceCount({ folders: [], devices: [device('a')] }), 1);
  assert.equal(
    deviceCount({ folders: [folder('outer', [folder('inner', [], [device('deep')])])], devices: [device('top')] }),
    2,
  );
});

test('the client opens on the first Site that has a device, not simply the first Site', async () => {
  // The seeded shape: Bitola sorts first and holds only an empty folder.
  const trees = {
    bitola: { folders: [folder('water-works')], devices: [] },
    skopje: { folders: [], devices: [device('pump-house')] },
  };
  const loaded = [];

  const chosen = await siteToOpen([bitola, skopje], async (id) => {
    loaded.push(id);
    return trees[id];
  });

  assert.equal(chosen, 'skopje');
  assert.deepEqual(loaded, ['bitola', 'skopje']);
});

test('a device found in a folder counts, and later Sites are not loaded', async () => {
  const loaded = [];
  const chosen = await siteToOpen([bitola, skopje], async (id) => {
    loaded.push(id);
    return { folders: [folder('water-works', [], [device('in-a-folder')])], devices: [] };
  });

  assert.equal(chosen, 'bitola');
  assert.deepEqual(loaded, ['bitola']);
});

test('with no device anywhere it falls back to the first Site, and with no Site to none', async () => {
  const empty = async () => ({ folders: [], devices: [] });
  assert.equal(await siteToOpen([bitola, skopje], empty), 'bitola');
  assert.equal(await siteToOpen([], empty), null);
});

test('an alarm row names its Site', () => {
  assert.equal(siteName([bitola, skopje], 'skopje'), 'Skopje');
  assert.equal(siteName([bitola, skopje], 'elsewhere'), '—');
});
