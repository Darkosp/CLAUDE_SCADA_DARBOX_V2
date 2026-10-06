// Exhaustive scan: every run of non-ASCII bytes in the tree, grouped by the exact byte sequence.
//
// Written because a first scanner produced a false positive that cost real time. It searched for the
// TEXT 'Â·', which is what `'·'.toString('latin1')` produces — but latin1 cannot represent U+00B7, so
// the comparison matched the CORRECT UTF-8 for `·` (C2 B7) and reported 14 healthy files as damaged.
//
// The lesson, and the reason this tool works on hex: **a check written in terms of decoded characters
// cannot be trusted about bytes that fail to decode.** Print the sequences and read them.
//
// Usage: node tools/scan-non-ascii.mjs [dir ...]

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const search = process.argv.slice(2);
const roots = search.length > 0 ? search : ['src', 'docs', 'tools'];
const extensions = /\.(ts|mjs|md|html|css|json|cs|sql)$/;

/** Every maximal run of bytes >= 0x80, with where it was found and what it decodes to. */
const runs = new Map();

function record(sequence, decoded, path, offset) {
  const key = [...sequence].map((b) => b.toString(16).padStart(2, '0')).join(' ');

  if (!runs.has(key)) {
    runs.set(key, { decoded, count: 0, files: new Set(), sample: null });
  }

  const entry = runs.get(key);
  entry.count++;
  entry.files.add(path);

  if (entry.sample === null) {
    const from = Math.max(0, offset - 30);
    entry.sample = readFileSync(path).subarray(from, offset + sequence.length + 20).toString('utf8');
  }
}

function* walk(directory) {
  for (const entry of readdirSync(directory)) {
    if (['node_modules', 'dist', '.git', '.node-test', 'bin', 'obj'].includes(entry)) continue;

    const path = join(directory, entry);

    if (statSync(path).isDirectory()) yield* walk(path);
    else if (extensions.test(path)) yield path;
  }
}

let scanned = 0;

for (const root of roots) {
  for (const path of walk(root)) {
    scanned++;
    const bytes = readFileSync(path);

    for (let at = 0; at < bytes.length; ) {
      if (bytes[at] < 0x80) {
        at++;
        continue;
      }

      let end = at;

      while (end < bytes.length && bytes[end] >= 0x80) end++;

      const sequence = bytes.subarray(at, end);

      // A run may hold several characters; split it into UTF-8 sequences when it decodes, so that a
      // healthy em dash is not reported as the same thing as a damaged one.
      const text = sequence.toString('utf8');

      if (!text.includes('\uFFFD')) {
        for (const character of text) {
          record(Buffer.from(character, 'utf8'), character, path, at);
        }
      } else {
        record(sequence, text, path, at);
      }

      at = end;
    }
  }
}

console.log(`  scanned ${scanned} files\n`);

const rows = [...runs.entries()].sort((a, b) => b[1].count - a[1].count);

for (const [hex, entry] of rows) {
  const suspicious = /[\u00C2\u00C3\u00E2\uFFFD]/.test(entry.decoded);
  const flag = suspicious ? '  <-- SUSPICIOUS' : '';

  console.log(`  ${hex.padEnd(20)} ${entry.count.toString().padStart(4)}x  ${JSON.stringify(entry.decoded)}${flag}`);
  console.log(`      ${[...entry.files].slice(0, 3).join(', ')}${entry.files.size > 3 ? ` (+${entry.files.size - 3})` : ''}`);
}

console.log(`\n  ${rows.length} distinct non-ASCII sequences`);
console.log('  A byte sequence that is valid UTF-8 for the character it shows is NOT damage, whatever it');
console.log('  looks like when the file is read as cp1252. Only the SUSPICIOUS rows deserve a look.');
