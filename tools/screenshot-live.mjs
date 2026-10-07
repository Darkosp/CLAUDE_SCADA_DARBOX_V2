// Screenshots the *live* application, signed in, by driving a headless Edge over the DevTools
// protocol.
//
// Kept because a design change to this project cannot honestly be reviewed without one: Phase 8's
// walk learned that the expensive way, and this is the cheap version. It is an instrument, not a
// test — it asserts nothing and ships nothing.
//
// The browser profile goes to the system temp directory, never into the repository: an earlier
// version put it beside the output and a `git add -A` swept 1681 of Edge's own files into a commit.
//
// Usage:  node tools/screenshot-live.mjs <username> <password> <outDir>
// Env:    SCADA_URL (default http://localhost:8090)
//         SCADA_EDGE (default the usual Edge install path)

import { spawn } from 'node:child_process';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [user, password, outDir] = process.argv.slice(2);

if (!user || !password || !outDir) {
  console.error('usage: node tools/screenshot-live.mjs <username> <password> <outDir>');
  process.exit(2);
}

const BASE = process.env.SCADA_URL ?? 'http://localhost:8090';
const EDGE =
  process.env.SCADA_EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const PORT = Number(process.env.SCADA_CDP_PORT ?? 9333);

mkdirSync(outDir, { recursive: true });
const profile = mkdtempSync(join(tmpdir(), 'scada-shot-'));

const browser = spawn(
  EDGE,
  [
    '--headless=new',
    '--disable-gpu',
    '--hide-scrollbars',
    '--no-first-run',
    '--disable-extensions',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${profile}`,
    'about:blank',
  ],
  { stdio: 'ignore' },
);

/** The page target's web socket, once the browser has one. */
async function target() {
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
      const page = list.find((entry) => entry.type === 'page');
      if (page?.webSocketDebuggerUrl) return page.webSocketDebuggerUrl;
    } catch {
      // Still starting; the debugging port is not listening yet.
    }
    await sleep(500);
  }
  throw new Error('the browser never exposed a page target');
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

function send(method, params = {}) {
  const id = nextId++;
  socket.send(JSON.stringify({ id, method, params }));
  return new Promise((resolve) => pending.set(id, resolve));
}

async function evaluate(expression) {
  const result = await send('Runtime.evaluate', {
    expression,
    awaitPromise: true,
    returnByValue: true,
  });
  if (result.exceptionDetails) {
    throw new Error(result.exceptionDetails.exception?.description ?? 'evaluation failed');
  }
  return result.result?.value;
}

async function shot(name) {
  const { data } = await send('Page.captureScreenshot', {
    format: 'png',
    captureBeyondViewport: true,
  });
  writeFileSync(join(outDir, `${name}.png`), Buffer.from(data, 'base64'));
  console.log(`  ${name}.png`);
}

/** Clicks the first button whose text starts with `text`, and says whether it found one. */
const clickButton = (text) => `
  (() => {
    const b = [...document.querySelectorAll('button')]
      .find((x) => x.textContent.trim().startsWith(${JSON.stringify(text)}));
    if (!b) return false;
    b.click();
    return true;
  })()
`;

await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', {
  // 1920x1080 by default, because that is the size a control-room panel actually is, and judging a
  // console on a laptop-sized viewport answers the wrong question. Override with SCADA_WIDTH and
  // SCADA_HEIGHT when a smaller shot is what is wanted.
  width: Number(process.env.SCADA_WIDTH ?? 1920),
  height: Number(process.env.SCADA_HEIGHT ?? 1080),
  deviceScaleFactor: 1,
  mobile: false,
});

console.log('signing in…');

// The theme is a `localStorage` choice that `index.html` reads before the first paint, so it has to
// be seeded on the origin BEFORE navigating — setting it afterwards would capture the flash rather
// than the theme. SCADA_THEME unset means "whatever the browser's own preference is".
const theme = process.env.SCADA_THEME;
if (theme === 'dark' || theme === 'light') {
  await send('Page.navigate', { url: BASE });
  await sleep(1500);
  await evaluate(`window.localStorage.setItem('scada.theme', ${JSON.stringify(theme)}), true`);
  console.log(`theme: ${theme}`);
}

await send('Page.navigate', { url: BASE });
await sleep(3000);

// Driven the way a person drives it: set the field through its own setter and fire the event
// Angular listens for. Assigning `.value` alone leaves the model untouched and the button disabled.
await evaluate(`
  (() => {
    const set = (name, value) => {
      const el = document.querySelector('input[name="' + name + '"]');
      if (!el) throw new Error('no ' + name + ' field');
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

const heading = await evaluate(`document.body.innerText.slice(0, 90)`);
console.log('signed in as:', JSON.stringify(heading.replace(/\n/g, ' ')));

// The screens view is the one an operator actually lives on, so it is captured first and by name.
if (await evaluate(clickButton('Screens'))) {
  await sleep(2500);
  await shot('live-screens');
}

for (const [name, label] of [
  ['live-browse', 'Browse'],
  ['live-journal', 'Journal'],
  ['live-templates', 'Templates'],
  ['live-users', 'Users'],
  ['live-edges', 'Edges'],
]) {
  if (await evaluate(clickButton(label))) {
    await sleep(2200);
    await shot(name);
  }
}

// The trend, which is the one surface whose *data* a later change moved (ADR-0029): one tag selected,
// then every window the picker offers, because both the caption and the envelope depend on how much of
// the window holds readings at all. A 15-minute window and a 7-day one are the two ends of that.
if (await evaluate(clickButton('Browse'))) {
  await sleep(2200);

  const picked = await evaluate(`
    (() => {
      const tag = document.querySelector('button.tag');
      if (!tag) return false;
      tag.click();
      return true;
    })()
  `);

  if (picked) {
    await sleep(2500);

    for (const window of ['15 minutes', '1 hour', '8 hours', '24 hours', '7 days']) {
      // Driven the way a person drives it: the select's own setter, then the event Angular listens for.
      // Assigning `.value` alone leaves the model untouched and the chart on its previous window.
      const chosen = await evaluate(`
        (() => {
          const select = document.querySelector('select[name="trendWindow"]');
          if (!select) return false;
          Object.getOwnPropertyDescriptor(Object.getPrototypeOf(select), 'value')
            .set.call(select, ${JSON.stringify(window)});
          select.dispatchEvent(new Event('change', { bubbles: true }));
          return true;
        })()
      `);

      if (chosen) {
        // Long enough for the fetch and for the chart to draw: the same wait the picker's own walk used.
        await sleep(3000);
        await shot(`live-trend-${window.replace(/ /g, '-')}`);
      }
    }
  }
}

// The write dialog: the newest surface, and the only one that changes a plant (ADR-0026).
if (await evaluate(clickButton('Screens'))) {
  await sleep(2200);
  if (await evaluate(clickButton('Write'))) {
    await sleep(1200);
    await shot('live-write-dialog');
  }

  if (await evaluate(clickButton('Edit'))) {
    await sleep(2800);
    await shot('live-editor');
  }
}

socket.close();
browser.kill();
console.log('done');
