// Finds every file carrying the mojibake a PowerShell round trip leaves, so a repair can be run over
// the whole tree rather than over a list someone remembered to write down.
//
// **Written because the list was wrong twice.** `§` was missed the first time because it is a
// two-byte character and the other two are three-byte — and it is the MOST common of the three in
// this repository, since every ADR reference with a decision number in it has one. Then a scanner that
// stored its patterns as hex TEXT was reported as damaged by its own run, because the text it searched
// for was in the file doing the searching. The patterns are built from bytes for that reason.
//
// Usage: node tools/find-mojibake.mjs [dir ...]

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

/** The damaged bytes for one character: the UTF-8 encoding of each byte of its own UTF-8. */
const doubled = (...bytes) => Buffer.from(bytes.flatMap((b) => {
  const encoded = Buffer.from([b]);

  // A byte below 0x80 is one UTF-8 byte; above it takes two. No damaged character here has a byte
  // above 0xBF, so the two-byte form is the only other case that arises.
  return b < 0x80 ? [b] : [0xc0 | (b >> 6), 0x80 | (b & 0x3f)];
}));

const PATTERNS = [
  { bytes: doubled(0xe2, 0x80, 0x94), describes: 'an em dash' },
  { bytes: doubled(0xe2, 0x80, 0xa6), describes: 'an ellipsis' },
  { bytes: doubled(0xc2, 0xa7), describes: 'a section sign' },
];

const roots = process.argv.slice(2);
const search = roots.length > 0 ? roots : ['src', 'docs', 'tools'];
const extensions = /\.(ts|mjs|md|html|css|json|cs|sql)$/;

const found = [];

function* walk(directory) {
  for (const entry of readdirSync(directory)) {
    if (['node_modules', 'dist', '.git', '.node-test', 'bin', 'obj'].includes(entry)) continue;

    const path = join(directory, entry);

    if (statSync(path).isDirectory()) yield* walk(path);
    else if (extensions.test(path)) yield path;
  }
}

for (const root of search) {
  for (const path of walk(root)) {
    const bytes = readFileSync(path);
    const hits = [];

    for (const { bytes: needle, describes } of PATTERNS) {
      let at = bytes.indexOf(needle);
      let count = 0;

      while (at >= 0) {
        count++;
        at = bytes.indexOf(needle, at + needle.length);
      }

      if (count > 0) hits.push(`${count}x ${describes}`);
    }

    if (hits.length > 0) found.push({ path, hits });
  }
}

for (const { path, hits } of found) {
  console.log(`  ${path}`);
  for (const hit of hits) console.log(`      ${hit}`);
}

console.log(`\n  ${found.length} file(s) with mojibake`);
process.exit(found.length > 0 ? 1 : 0);
