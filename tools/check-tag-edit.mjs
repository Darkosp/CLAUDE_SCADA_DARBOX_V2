// Checks that an existing tag can be edited from the browser, and that the edit travels as a PUT.
//
// It exists because it could not: until 2026-10-06 the client had `saveTag(deviceId, tagId, …)` and
// the API had the endpoint, and nothing opened an existing tag into the draft — so the only
// reachable operations on a tag were add and delete. A walk found it, which is the class of defect
// this tool exists to catch a second time.
//
// Usage: node tools/check-tag-edit.mjs <username> <password>

import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [user, password] = process.argv.slice(2);
if (!user || !password) {
  console.error('usage: node tools/check-tag-edit.mjs <username> <password>');
  process.exit(2);
}

const BASE = process.env.SCADA_URL ?? 'http://localhost:8090';
const EDGE =
  process.env.SCADA_EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const PORT = Number(process.env.SCADA_CDP_PORT ?? 9336);
const TAG = process.env.SCADA_TAG ?? 'Pump Running';

const browser = spawn(
  EDGE,
  [
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--disable-extensions',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${mkdtempSync(join(tmpdir(), 'scada-tagedit-'))}`,
    'about:blank',
  ],
  { stdio: 'ignore' },
);

async function target() {
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
      const page = list.find((entry) => entry.type === 'page');
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch {
      // Still starting.
    }
    await sleep(500);
  }
  throw new Error('no page target');
}

const socket = new WebSocket(await target());
await new Promise((ok) => socket.addEventListener('open', ok, { once: true }));

let nextId = 1;
const pending = new Map();
const methods = [];

socket.addEventListener('message', (event) => {
  const message = JSON.parse(event.data);

  // Every request the page makes, so the HTTP method can be read rather than assumed.
  if (message.method === 'Network.requestWillBeSent') {
    methods.push(`${message.params.request.method} ${message.params.request.url}`);
  }

  const resolve = pending.get(message.id);
  if (resolve) {
    pending.delete(message.id);
    resolve(message.result ?? {});
  }
});

const send = (method, params = {}) => {
  const id = nextId++;
  socket.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve) => pending.set(id, resolve));
};

async function evaluate(expression) {
  const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description);
  return result.result?.value;
}

const report = (label, value) => console.log(`  ${label.padEnd(40)} ${value}`);

await send('Page.enable');
await send('Runtime.enable');
await send('Network.enable');
await send('Emulation.setDeviceMetricsOverride', {
  width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false,
});

await send('Page.navigate', { url: BASE });
await sleep(3000);
await evaluate(`
  (() => {
    const set = (name, value) => {
      const el = document.querySelector('input[name="' + name + '"]');
      Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value').set.call(el, value);
      el.dispatchEvent(new Event('input', { bubbles: true }));
    };
    set('loginName', ${JSON.stringify(user)});
    set('loginPassword', ${JSON.stringify(password)});
    return true;
  })()
`);
await sleep(400);
await evaluate(`document.querySelector('button[type="submit"]').click(), true`);
await sleep(4000);

// Browse, and pick the tag by its name in the tree.
await evaluate(`
  (() => {
    const b = [...document.querySelectorAll('nav.views button')].find((x) => x.textContent.trim() === 'Browse');
    if (b) b.click();
    return true;
  })()
`);
await sleep(2000);

const picked = await evaluate(`
  (() => {
    const tag = [...document.querySelectorAll('app-browse-tree .tag')]
      .find((x) => x.textContent.trim().startsWith(${JSON.stringify(TAG)}));
    if (!tag) return 'tag not found in the tree';
    tag.click();
    return 'picked ' + tag.textContent.trim();
  })()
`);
report('the tree', picked);
await sleep(1200);

const buttons = await evaluate(`
  [...document.querySelectorAll('.pane-head .actions button')].map((b) => b.textContent.trim())
`);
report('buttons offered', JSON.stringify(buttons));

const hasEditTag = Array.isArray(buttons) && buttons.includes('Edit tag');
report('Edit tag is offered', hasEditTag ? 'YES' : 'NO -- the defect is still here');

if (!hasEditTag) {
  socket.close();
  browser.kill();
  process.exit(1);
}

// Open it, and check the form arrived filled rather than blank.
await evaluate(`
  (() => {
    const b = [...document.querySelectorAll('.pane-head .actions button')].find((x) => x.textContent.trim() === 'Edit tag');
    b.click();
    return true;
  })()
`);
await sleep(1200);

const form = await evaluate(`
  (() => {
    const value = (name) => document.querySelector('input[name="' + name + '"]')?.value ?? null;
    return {
      name: value('tagLabel'),
      sourceAddress: value('tagSourceAddress') ?? value('sourceAddress'),
      writable: document.querySelector('input[name="writable"]')?.checked ?? null,
      inputs: [...document.querySelectorAll('form.form input')].map((i) => i.name || i.type),
    };
  })()
`);
report('the form arrived filled', JSON.stringify(form));

// Toggle writable and save, then read back what the page actually sent.
methods.length = 0;
await evaluate(`
  (() => {
    const box = document.querySelector('input[name="writable"]');
    if (!box) return 'no writable checkbox';
    box.click();
    return 'toggled';
  })()
`);
await sleep(300);
await evaluate(`
  (() => {
    const save = [...document.querySelectorAll('form.form button')].find((x) => x.textContent.trim() === 'Save');
    if (!save) return 'no Save';
    save.click();
    return true;
  })()
`);
await sleep(3000);

const wrote = methods.filter((m) => m.includes('/tags/') && !m.startsWith('GET'));
report('requests to the tags API', JSON.stringify(wrote));
report('a PUT was sent', wrote.some((m) => m.startsWith('PUT')) ? 'YES' : 'NO');

socket.close();
browser.kill();
console.log('');
