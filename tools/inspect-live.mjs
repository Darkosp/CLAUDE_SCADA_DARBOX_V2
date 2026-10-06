// Reads computed styles out of the running client over the DevTools protocol, for the questions a
// screenshot cannot answer: which token actually won, and what a box really measures.
//
// Instrument, like its siblings. Usage:
//   node tools/inspect-live.mjs <username> <password> "<selector>" [property ...]

import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [user, password, selector, ...props] = process.argv.slice(2);
if (!user || !password || !selector) {
  console.error('usage: node tools/inspect-live.mjs <username> <password> "<selector>" [property ...]');
  process.exit(2);
}

const BASE = process.env.SCADA_URL ?? 'http://localhost:8090';
const EDGE =
  process.env.SCADA_EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const PORT = Number(process.env.SCADA_CDP_PORT ?? 9334);
const properties = props.length > 0 ? props : ['background-color', 'color', 'font-size'];

const browser = spawn(
  EDGE,
  [
    '--headless=new',
    '--disable-gpu',
    '--no-first-run',
    '--disable-extensions',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${mkdtempSync(join(tmpdir(), 'scada-inspect-'))}`,
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
  width: 1920,
  height: 1080,
  deviceScaleFactor: 1,
  mobile: false,
});

const theme = process.env.SCADA_THEME;
if (theme) {
  await send('Page.navigate', { url: BASE });
  await sleep(1500);
  await evaluate(`window.localStorage.setItem('scada.theme', ${JSON.stringify(theme)}), true`);
}

await send('Page.navigate', { url: BASE });
await sleep(3000);

await evaluate(`
  (() => {
    const set = (name, value) => {
      const el = document.querySelector('input[name="' + name + '"]');
      const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value').set;
      setter.call(el, value);
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

// The screens view is not necessarily where a session lands, and it is the view these questions are
// almost always about, so it is opened explicitly. SCADA_VIEW overrides for another one.
const view = process.env.SCADA_VIEW ?? 'Screens';
await evaluate(`
  (() => {
    const b = [...document.querySelectorAll('nav.views button')]
      .find((x) => x.textContent.trim() === ${JSON.stringify(view)});
    if (b) b.click();
    return !!b;
  })()
`);
await sleep(2500);

const report = await evaluate(`
  (() => {
    const host = document.querySelector(${JSON.stringify(selector)});
    const describe = (el) =>
      el.tagName.toLowerCase() + (el.className ? '.' + String(el.className).split(' ').join('.') : '');

    if (!host) {
      return {
        error: 'no element matches ' + ${JSON.stringify(selector)},
        theme: document.documentElement.getAttribute('data-theme'),
        view: ${JSON.stringify(view)},
        appRootChildren: [...document.querySelectorAll('app-root *')].slice(0, 25).map(describe),
        bodyText: document.body.innerText.slice(0, 220),
      };
    }

    const style = getComputedStyle(host);
    const box = host.getBoundingClientRect();
    const root = getComputedStyle(document.documentElement);

    const wanted = ${JSON.stringify(properties)};
    const computed = {};
    for (const name of wanted) computed[name] = style.getPropertyValue(name);

    return {
      theme: document.documentElement.getAttribute('data-theme'),
      selector: ${JSON.stringify(selector)},
      tag: host.tagName.toLowerCase(),
      classes: host.className,
      box: { width: Math.round(box.width), height: Math.round(box.height) },
      computed,
      tokens: {
        '--surface': root.getPropertyValue('--surface').trim(),
        '--surface-page': root.getPropertyValue('--surface-page').trim(),
        '--surface-sunken': root.getPropertyValue('--surface-sunken').trim(),
        '--text': root.getPropertyValue('--text').trim(),
        '--text-muted': root.getPropertyValue('--text-muted').trim(),
      },
    };
  })()
`);

console.log(JSON.stringify(report, null, 2));

socket.close();
browser.kill();
