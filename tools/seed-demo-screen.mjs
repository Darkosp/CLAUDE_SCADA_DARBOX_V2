// Builds a realistic demo screen on a running stack, so a design can be judged against something
// that looks like a plant rather than against two seeded rows. An instrument, like its sibling
// `screenshot-live.mjs` — it asserts nothing and ships nothing.
//
// Usage: node tools/seed-demo-screen.mjs <username> <password>

const [user, password] = process.argv.slice(2);

if (!user || !password) {
  console.error('usage: node tools/seed-demo-screen.mjs <username> <password>');
  process.exit(2);
}

const BASE = process.env.SCADA_URL ?? 'http://localhost:8090';
const SITE = process.env.SCADA_SITE ?? '0f7a1b2c-0000-4000-8000-000000000002';

const login = await fetch(`${BASE}/api/auth/login`, {
  method: 'POST',
  headers: { 'content-type': 'application/json' },
  body: JSON.stringify({ username: user, password }),
}).then((r) => r.json());

const H = { authorization: `Bearer ${login.token}`, 'content-type': 'application/json' };

const api = async (method, path, body) => {
  const response = await fetch(`${BASE}${path}`, {
    method,
    headers: H,
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await response.text();
  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status} ${text}`);
  return text ? JSON.parse(text) : null;
};

const tree = await api('GET', `/api/sites/${SITE}/tree`);
const device = tree.devices[0];

const tagIdFrom = (list, name) => {
  const tag = list.find((t) => t.name === name);
  if (!tag) throw new Error(`no tag called ${name}`);
  return tag.id;
};

// A few more readings, so a screen has something to be a screen about. Idempotent: a tag that
// already exists is reused rather than refused.
const ensureTag = async (name, valueKind, sourceAddress, unitSymbol, isWritable = false) => {
  const existing = device.tags.find((t) => t.name === name);
  if (existing) return existing.id;

  const created = await api('POST', `/api/devices/${device.id}/tags`, {
    name,
    valueKind,
    sourceAddress,
    isWritable,
    unit: unitSymbol
      ? {
          symbol: unitSymbol,
          dimension: unitSymbol === '%' ? 'Dimensionless' : 'Pressure',
          siFactor: unitSymbol === '%' ? 1 : 100000,
        }
      : null,
  });
  return created.id;
};

const level = await ensureTag('Tank 3 Level', 'Numeric', 'holding:10', '%');
const flow = await ensureTag('Suction Flow', 'Numeric', 'holding:11', 'bar');
const setpoint = await ensureTag('Discharge Setpoint', 'Numeric', 'holding:12', 'bar', true);

// The tree read above is now stale — it predates the tags just created — so it is read AGAIN before
// any id is looked up in it. This cost one run: three components were sent with `tagId: undefined`,
// and the server refused them with "A 'value' component reads a tag and needs one", which was the
// right answer to the wrong input.
const fresh = await api('GET', `/api/sites/${SITE}/tree`);
const tags = fresh.devices.find((d) => d.id === device.id).tags;
const tagId = (name) => {
  const tag = tags.find((t) => t.name === name);
  if (!tag) throw new Error(`no tag called ${name}`);
  return tag.id;
};

const pressure = tagId('Discharge Pressure');
const running = tagId('Pump Running');

const screens = await api('GET', `/api/sites/${SITE}/screens`);
const screenId = screens[0].id;
const current = await api('GET', `/api/screens/${screenId}`);

// Laid out the way an operator would build it: a heading, a row of readings with the pump state
// first, a symbol, a trend and the alarm list beneath. `id: null` means "the server assigns one".
const components = [
  { id: null, rowIndex: 0, columnSpan: 12, position: 0, kind: 'label', title: 'Pump House — live', tagId: null },
  { id: null, rowIndex: 1, columnSpan: 3, position: 0, kind: 'value', title: null, tagId: pressure },
  { id: null, rowIndex: 1, columnSpan: 3, position: 1, kind: 'value', title: null, tagId: level },
  { id: null, rowIndex: 1, columnSpan: 3, position: 2, kind: 'value', title: null, tagId: flow },
  { id: null, rowIndex: 1, columnSpan: 3, position: 3, kind: 'value', title: null, tagId: running },
  {
    // ADR-0027: a picture of the machine rather than the word for its state. The mapping is the one a
    // new symbol is born with, so this is what an author gets without editing anything.
    id: null,
    rowIndex: 2,
    columnSpan: 3,
    position: 0,
    kind: 'symbol',
    title: null,
    tagId: running,
    symbol: 'pump',
    states: [
      { state: 'running', when: 'equals', value: 'true', otherwise: false },
      { state: 'stopped', when: null, value: null, otherwise: true },
    ],
  },
  { id: null, rowIndex: 2, columnSpan: 5, position: 1, kind: 'trend', title: null, tagId: pressure },
  { id: null, rowIndex: 2, columnSpan: 4, position: 2, kind: 'alarms', title: 'Standing alarms', tagId: null },
];

await api('PUT', `/api/screens/${screenId}`, {
  name: current.name,
  siteId: SITE,
  components,
});

console.log(`screen "${current.name}" now holds ${components.length} components`);
console.log(`  writable tag for the write control: Discharge Setpoint ${setpoint}`);
