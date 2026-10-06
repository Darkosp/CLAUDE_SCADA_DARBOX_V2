// A visual WCAG audit of the running client, in whichever theme it is in.
//
// **It measures what is on the screen, not what the stylesheet says.** `theme-contrast.test.mjs`
// already checks the token pairs, and that is the cheaper and more reliable check — but it can only
// see pairs somebody thought to enumerate. This walks the rendered DOM, takes every element's own
// computed colour and its real painted background, and computes the ratio. It finds the pairs nobody
// wrote down, which is the class of defect a palette test cannot catch.
//
// It reports rather than asserts: there is no single ratio that decides whether a screen is readable,
// and it does not know which text is decorative. The judgement stays with a person; this is the
// measurement they judge from.
//
// Usage: SCADA_THEME=dark node tools/audit-contrast.mjs <username> <password>

import { spawn } from 'node:child_process';
import { mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { setTimeout as sleep } from 'node:timers/promises';

const [user, password] = process.argv.slice(2);
if (!user || !password) {
  console.error('usage: SCADA_THEME=dark node tools/audit-contrast.mjs <username> <password>');
  process.exit(2);
}

const BASE = process.env.SCADA_URL ?? 'http://localhost:8090';
const THEME = process.env.SCADA_THEME ?? 'dark';
const EDGE =
  process.env.SCADA_EDGE ?? 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';
const PORT = Number(process.env.SCADA_CDP_PORT ?? 9337);
const VIEW = process.env.SCADA_VIEW ?? 'Screens';

const browser = spawn(
  EDGE,
  [
    '--headless=new', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--disable-extensions',
    `--remote-debugging-port=${PORT}`,
    `--user-data-dir=${mkdtempSync(join(tmpdir(), 'scada-contrast-'))}`,
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

// The theme is applied before the first paint by index.html, so it has to be set before navigating.
await send('Page.navigate', { url: `${BASE}/?theme=${THEME}` });
await sleep(1500);
await evaluate(`localStorage.setItem('scada.theme', ${JSON.stringify(THEME)}), true`);
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

if (VIEW) {
  await evaluate(`
    (() => {
      const b = [...document.querySelectorAll('nav.views button')].find((x) => x.textContent.trim() === ${JSON.stringify(VIEW)});
      if (b) b.click();
      return true;
    })()
  `);
  await sleep(3000);
}

// Runs in the page: every visible text-bearing element, its own colour, and the colour actually
// painted behind it.
const MEASURE = `
  (() => {
    const parse = (colour) => {
      const m = colour.match(/rgba?\\(([^)]+)\\)/);
      if (!m) return null;
      const parts = m[1].split(',').map((x) => parseFloat(x.trim()));
      return { r: parts[0], g: parts[1], b: parts[2], a: parts.length > 3 ? parts[3] : 1 };
    };

    const luminance = ({ r, g, b }) => {
      const channel = (v) => {
        const s = v / 255;
        return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
      };
      return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
    };

    const ratio = (a, b) => {
      const la = luminance(a), lb = luminance(b);
      return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
    };

    // The first ancestor with a non-transparent background is what the text is painted on.
    const behind = (element) => {
      for (let node = element; node; node = node.parentElement) {
        const colour = parse(getComputedStyle(node).backgroundColor);
        if (colour && colour.a > 0) return colour;
      }
      return { r: 255, g: 255, b: 255, a: 1 };
    };

    const results = [];

    for (const element of document.querySelectorAll('body *')) {
      // Only elements that hold their own text, so a wrapper is not measured for its children's.
      const own = [...element.childNodes]
        .filter((n) => n.nodeType === 3)
        .map((n) => n.textContent.trim())
        .join(' ')
        .trim();

      if (!own) continue;

      const style = getComputedStyle(element);

      if (style.visibility === 'hidden' || style.display === 'none' || parseFloat(style.opacity) < 0.1) continue;

      const rect = element.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) continue;

      const fg = parse(style.color);
      const bg = behind(element);
      if (!fg || !bg) continue;

      // A translucent foreground is composited over the background before measuring.
      const flat = {
        r: fg.r * fg.a + bg.r * (1 - fg.a),
        g: fg.g * fg.a + bg.g * (1 - fg.a),
        b: fg.b * fg.a + bg.b * (1 - fg.a),
      };

      const size = parseFloat(style.fontSize);
      const weight = parseInt(style.fontWeight, 10) || 400;
      const bold = weight >= 700;
      // WCAG's "large text": 18.66px bold or 24px regular.
      const large = size >= 24 || (bold && size >= 18.66);

      results.push({
        text: own.slice(0, 44),
        tag: element.tagName.toLowerCase(),
        cls: (element.className || '').toString().slice(0, 34),
        colour: style.color,
        background: 'rgb(' + Math.round(bg.r) + ', ' + Math.round(bg.g) + ', ' + Math.round(bg.b) + ')',
        size: Math.round(size * 10) / 10,
        bold,
        large,
        ratio: Math.round(ratio(flat, bg) * 100) / 100,
        needed: large ? 3 : 4.5,
      });
    }

    /**
     * Graphical objects, which WCAG 1.4.11 holds to 3:1 rather than 4.5.
     *
     * **The symbol is the reason this is here.** A pump is a drawing, and its whole job is to be
     * readable at a glance — so a stroke that fails 3:1 against the card behind it is a defect no
     * text audit can see. Measured on the stroke colours actually computed, not on the palette.
     */
    for (const shape of document.querySelectorAll('svg .body, svg .vanes line, svg .pipe, svg .cross, svg .hub, .chart path, .chart line, .chart polyline')) {
      const style = getComputedStyle(shape);

      const stroke = parse(style.stroke);
      const fill = parse(style.fill);

      const paint = stroke && stroke.a > 0 ? stroke : (fill && fill.a > 0 ? fill : null);
      if (!paint) continue;

      const rect = shape.getBoundingClientRect();
      if (rect.width === 0 && rect.height === 0) continue;

      const bg = behind(shape);
      const flat = {
        r: paint.r * paint.a + bg.r * (1 - paint.a),
        g: paint.g * paint.a + bg.g * (1 - paint.a),
        b: paint.b * paint.a + bg.b * (1 - paint.a),
      };

      results.push({
        text: shape.getAttribute('class') || shape.tagName.toLowerCase(),
        tag: 'svg',
        cls: (shape.getAttribute('class') || '').toString(),
        colour: stroke && stroke.a > 0 ? style.stroke : style.fill,
        background: 'rgb(' + Math.round(bg.r) + ', ' + Math.round(bg.g) + ', ' + Math.round(bg.b) + ')',
        size: 0,
        bold: false,
        large: true,
        ratio: Math.round(ratio(flat, bg) * 100) / 100,
        needed: 3,
        graphic: true,
      });
    }

    return results;
  })()
`;

const measured = await evaluate(MEASURE);

// Read the page background BEFORE closing the socket — the first version read it after, and the
// evaluate never settled, so the audit printed nothing and the run looked like a failure of the page.
const themeColour = await evaluate(`getComputedStyle(document.body).backgroundColor`);

socket.close();
browser.kill();

if (!Array.isArray(measured)) {
  console.error('  the page did not return measurements');
  process.exit(1);
}

const failing = measured.filter((m) => m.ratio < m.needed);
const borderline = measured.filter((m) => m.ratio >= m.needed && m.ratio < m.needed + 1);
const graphics = measured.filter((m) => m.graphic);

console.log(`\n  theme "${THEME}" on the ${VIEW} view — ${measured.length} elements measured`);
if (themeColour) console.log(`  page background ${themeColour}\n`);

console.log(`  GRAPHICAL OBJECTS (WCAG 1.4.11, needs 3:1): ${graphics.length}`);
for (const m of graphics.sort((a, b) => a.ratio - b.ratio)) {
  const flag = m.ratio < m.needed ? '  <-- BELOW' : '';
  console.log(`    ${m.ratio.toFixed(2)}:1  ${m.cls.padEnd(14)} ${m.colour} on ${m.background}${flag}`);
}

console.log(`  BELOW WCAG AA: ${failing.length}`);
for (const m of failing.sort((a, b) => a.ratio - b.ratio)) {
  console.log(
    `    ${m.ratio.toFixed(2)}:1  (needs ${m.needed})  ${m.tag}.${m.cls}  ${m.size}px${m.bold ? ' bold' : ''}`,
  );
  console.log(`        "${m.text}"`);
  console.log(`        text ${m.colour} on ${m.background}`);
}

console.log(`\n  BORDERLINE (within 1.0 of the requirement): ${borderline.length}`);
for (const m of borderline.sort((a, b) => a.ratio - b.ratio).slice(0, 12)) {
  console.log(`    ${m.ratio.toFixed(2)}:1  (needs ${m.needed})  ${m.tag}.${m.cls}  ${m.size}px`);
  console.log(`        "${m.text}"`);
}

console.log('');
process.exit(failing.length > 0 ? 1 : 0);
