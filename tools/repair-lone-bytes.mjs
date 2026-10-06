// Repairs two single characters that are damaged in ways `repair-encoding.mjs` cannot see, because
// neither is a multi-byte character that was double-encoded — each is one byte that is not valid
// UTF-8 at all, in a file that is otherwise fine.
//
//   src/Web/tests/screen-resolution.test.mjs   "ADR-0024 §9"  the `§` is a lone byte 0xA7
//   docs/roadmap/walk-2026-10-06.md            "1920×1080"    the `×` is U+FFFD, a replacement mark
//
// **The first of these was caused by an earlier repair in this session**, and that is the part worth
// recording. `repair-encoding.mjs` worked on bytes and was right about the pairs it knew; what it could
// not know was that one file also held a lone high byte, and a byte-level rewrite can carry such a byte
// forward unchanged while looking like it succeeded. The lesson is the one this repository keeps
// relearning: **verify the whole result, not the part you changed** — which is why the scan that found
// these prints every non-ASCII byte sequence in the tree rather than only the ones being looked for.
//
// Usage: node tools/repair-lone-bytes.mjs

import { readFileSync, writeFileSync } from 'node:fs';

const REPAIRS = [
  {
    path: 'src/Web/tests/screen-resolution.test.mjs',
    // **Written with a `\u00a7` escape rather than a hex literal, and that distinction cost a run.**
    // In a template or a plain string `\u00a7` is the CHARACTER §, which encodes to two bytes — the
    // whole point being that the file has only ONE. A single byte has to be built as a buffer.
    find: Buffer.concat([Buffer.from('ADR-0024 '), Buffer.from([0xa7]), Buffer.from('9')]),
    replace: Buffer.from('ADR-0024 \u00a79', 'utf8'),
    describes: 'the section sign in "ADR-0024 §9"',
  },
  {
    path: 'docs/roadmap/walk-2026-10-06.md',
    // No space around the damaged bytes: the file reads `1920` then the mark then `1080`. Assuming a
    // separator that is not there is what made the first attempt report "not found" — which is the
    // correct outcome, and better than a repair that matched something nearby.
    find: Buffer.concat([
      Buffer.from('1920'),
      Buffer.from([0xef, 0xbf, 0xbd, 0xe2, 0x80, 0x94]),
      Buffer.from('1080'),
    ]),
    replace: Buffer.from('1920\u00d71080', 'utf8'),
    describes: 'the multiplication sign in "1920×1080"',
  },
];

let repaired = 0;
let refused = 0;

for (const { path, find, replace, describes } of REPAIRS) {
  const original = readFileSync(path);
  const at = original.indexOf(find);

  if (at < 0) {
    console.log(`  not found   ${path}: ${describes}`);
    refused++;
    continue;
  }

  const next = Buffer.concat([original.subarray(0, at), replace, original.subarray(at + find.length)]);

  // The result must decode, and must contain the character the repair is for.
  try {
    new TextDecoder('utf-8', { fatal: true }).decode(next);
  } catch (error) {
    console.log(`  REFUSED     ${path}: the result is not valid UTF-8 (${error.message})`);
    refused++;
    continue;
  }

  const wanted = replace.toString('utf8');
  const text = next.toString('utf8');

  if (!text.includes(wanted)) {
    console.log(`  REFUSED     ${path}: ${JSON.stringify(wanted)} is not in the result`);
    refused++;
    continue;
  }

  writeFileSync(path, next);
  console.log(`  repaired    ${path}  ${describes}`);
  repaired++;
}

console.log(`\n  ${repaired} repaired, ${refused} refused`);
process.exit(refused > 0 ? 1 : 0);
