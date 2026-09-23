// Plain JavaScript and Node's own test runner, like the other tests here (see
// parse-number-field.test.mjs for why).
//
// Since Phase 6 the gateway serves this client, so the API and the live hub are on the
// page's own origin and every request uses a relative path. The client used to carry
// `http://localhost:5220`, which works on a developer's machine and nowhere else: in a
// container, or from any other machine on the plant network, "localhost" is the browser's
// own computer. This fails if an absolute address comes back into the application source.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(new URL('../src/', import.meta.url));

/** Every .ts file under src/, recursively. */
function sources(directory) {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return sources(path);
    }
    return entry.name.endsWith('.ts') ? [path] : [];
  });
}

test('the application source addresses the gateway by relative path only', () => {
  const files = sources(source);

  // Something was scanned, so "nothing found" below means something.
  assert.ok(files.some((file) => file.endsWith('api.ts')), 'api.ts was not among the scanned files');

  const absolute = /\b(?:https?|wss?):\/\/|\blocalhost\b/;
  const offenders = files.flatMap((file) =>
    readFileSync(file, 'utf8')
      .split('\n')
      .map((line, index) => ({ file, line: index + 1, text: line.trim() }))
      .filter(({ text }) => absolute.test(text)),
  );

  assert.deepEqual(offenders, [], 'absolute addresses in the client source');
});
