// Checks the authoring path end to end: can an author add a symbol to a screen through the browser,
// and does what they built survive a save?
//
// This exists because the symbol's data path was verified and the *authoring* path was not — the
// editor's form was seen in a screenshot of an existing symbol, which is not the same thing as an
// author building one from nothing. Instrument, like its siblings: it asserts nothing and ships
// nothing, but it prints what it found rather than a tick.
//
// Usage: node tools/check-authoring.mjs <username> <password>

import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [user, password] = process.argv.slice(2);
if (!user || !password) {
  console.error('usage: node tools/check-authoring.mjs <username> <password>');
  process.exit(2);
}

const BASE = process.env.SCADA_URL ?? 'http://localhost:8090';
const EDGE =
  process.env.SCADA_EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const PORT = Number(process.env.SCADA_CDP_PORT ?? 9335);
const SITE = process.env.SCADA_SITE ?? '0f7a1b2c-0000-4000-8000-000000000002';

const browser = spawn(
  EDGE,
  [
    '--headless=new',
    '--disable-gpu',
    '--hide-scrollbars',
    '--no-first-run',
    '--disable-extensions',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${mkdtempSync(join(tmpdir(), 'scada-author-'))}`,
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
socket.addEventListener('message', (event) => {
  const message = JSON.parse(event.data);
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

await send('Page.enable');
await send('Runtime.enable');
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

const report = (label, value) => console.log(`  ${label.padEnd(46)} ${value}`);

/** Reads the draft's own state out of the DOM, which is what an author is actually looking at. */
const draftCells = `
  [...document.querySelectorAll('app-screen-editor .cell')].map((cell) => ({
    head: cell.querySelector('.cell-head strong')?.textContent.trim() ?? '?',
    rules: [...cell.querySelectorAll('.rule')].length,
    shape: cell.querySelector('.mapping .shape select')?.value ?? null,
  }))
`;

console.log('\nBEFORE — what the screen holds');
await evaluate(`
  (() => {
    const b = [...document.querySelectorAll('nav.views button')].find((x) => x.textContent.trim() === 'Screens');
    if (b) b.click();
    return true;
  })()
`);
await sleep(2500);

/**
 * The cells the editor can actually read a head from.
 *
 * The draft's `@for` leaves trailing comment anchors in the DOM, so the last elements are unnamed
 * placeholders rather than components — which made this report claim the new symbol was not a symbol
 * on its first run. Counting from the end without this would have been wrong in the direction that
 * matters: a false failure is cheaper than a false pass, but both are noise.
 */
const namedCells = async () => (await evaluate(draftCells)).filter((cell) => cell.head !== '?');
const symbolsIn = (cells) => cells.filter((cell) => cell.head === 'Symbol');

const before = await namedCells();
report('cells before', JSON.stringify(before.map((cell) => cell.head)));
report('symbols before', symbolsIn(before).length);

// Open the editor, pick Symbol, bind a tag, and add it — the whole path an author takes.
console.log('\nAUTHORING a symbol from the picker');
const added = await evaluate(`
  (() => {
    const edit = [...document.querySelectorAll('button')].find((x) => x.textContent.trim() === 'Edit');
    if (!edit) return 'no Edit button';
    edit.click();
    return 'opened the editor';
  })()
`);
report('editor', added);
await sleep(2000);

const picked = await evaluate(`
  (() => {
    const kind = [...document.querySelectorAll('app-screen-editor select')]
      .find((s) => [...s.options].some((o) => o.value === 'symbol'));
    if (!kind) return 'no kind picker offers symbol';
    kind.value = 'symbol';
    kind.dispatchEvent(new Event('change', { bubbles: true }));
    kind.dispatchEvent(new Event('input', { bubbles: true }));
    return 'picked symbol';
  })()
`);
report('kind picker', picked);
await sleep(800);

const bound = await evaluate(`
  (() => {
    const tag = [...document.querySelectorAll('app-screen-editor select')]
      .find((s) => [...s.options].some((o) => o.value && o.value.length === 36));
    if (!tag) return 'no tag picker';
    const option = [...tag.options].find((o) => o.textContent.includes('Pump Running')) ?? tag.options[1];
    tag.value = option.value;
    tag.dispatchEvent(new Event('change', { bubbles: true }));
    tag.dispatchEvent(new Event('input', { bubbles: true }));
    return 'bound ' + option.textContent.trim();
  })()
`);
report('tag picker', bound);
await sleep(600);

const submitted = await evaluate(`
  (() => {
    const add = [...document.querySelectorAll('app-screen-editor button')]
      .find((x) => x.textContent.trim().startsWith('Add to row'));
    if (!add) return 'no Add button';
    add.click();
    return 'pressed ' + add.textContent.trim();
  })()
`);
report('add', submitted);
await sleep(1200);

console.log('\nAFTER — what the draft holds now');
const after = await namedCells();
const newSymbols = symbolsIn(after);

report('cells after', JSON.stringify(after.map((cell) => cell.head)));
report('symbols after', newSymbols.length);

// The question is not "is the last cell a symbol" -- a symbol is added to ROW ONE, so it lands second
// in a flattened list and an earlier run's symbols sit around it. The question is whether ONE MORE
// symbol exists, with a shape, and with rules enough that the API will accept it.
const appeared = newSymbols.length - symbolsIn(before).length;
const withRules = newSymbols.filter((cell) => cell.rules > 0).length;

report('a new symbol appeared', appeared === 1 ? 'YES' : `NO (${appeared})`);
report('it carries a shape', newSymbols.some((cell) => cell.shape === 'pump') ? 'YES (pump)' : 'NO');
report('it carries rules, so the API will take it', `${withRules} symbol(s) with rules`);

// The real question: does it save? The API refuses a symbol with no states, so a save that succeeds
// is proof the mapping travelled.
console.log('\nSAVING');
const problemsBefore = await evaluate(`document.querySelectorAll('app-screen-editor .problem').length`);
const saved = await evaluate(`
  (() => {
    const save = [...document.querySelectorAll('app-screen-editor button')]
      .find((x) => x.textContent.trim() === 'Save');
    if (!save) return 'no Save button';
    if (save.disabled) return 'Save was DISABLED, so nothing was sent';
    save.click();
    return 'pressed Save';
  })()
`);
report('save', saved);
await sleep(3500);

const problem = await evaluate(`
  document.querySelector('app-screen-editor .problem')?.textContent.trim() ?? null
`);
report('refusal shown', problem ?? 'none');
report('problems before the save', problemsBefore);

const leftEditor = await evaluate(`
  (() => {
    const b = [...document.querySelectorAll('nav.views button')].find((x) => x.textContent.trim() === 'Screens');
    if (b) b.click();
    return true;
  })()
`);
await sleep(2500);
const persisted = await evaluate(`
  [...document.querySelectorAll('app-screen .cell')].map((cell) => ({
    kind: cell.querySelector('.symbol-state') ? 'symbol' : (cell.querySelector('.reading') ? 'value' : 'other'),
    state: cell.querySelector('.symbol-state')?.textContent.trim() ?? null,
  }))
`);
report('after leaving the editor', leftEditor);
report('cells on the saved screen', JSON.stringify(persisted));

socket.close();
browser.kill();
console.log('');
