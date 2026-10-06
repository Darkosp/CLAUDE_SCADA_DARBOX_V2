// Repairs the mojibake a PowerShell round trip left in this repository's text files.
//
// **The two damaged sequences, established by scanning bytes rather than by guessing.** A UTF-8 file
// was read as Windows-1252 and written back as UTF-8, so each byte of a multi-byte character became a
// character of its own. Two characters were affected, and both are common in this repository's prose:
//
//   —  (E2 80 94)  became  â  €  "   = C3 A2  E2 82 AC  E2 80 9D
//   …  (E2 80 A6)  became  â  €  ¦   = C3 A2  E2 82 AC  C2 A6
//
// The damage is valid UTF-8, which is why nothing failed and why it survived: it renders as visible
// nonsense in a browser and as nothing at all in a terminal that does not print it.
//
// **Three attempts were needed and the first two are worth recording, because both were confident.**
// The first replaced the decoded TEXT 'â€' by reading the file as latin1 — which cannot round-trip
// U+20AC, so the repair could not be written. The second searched for 'Â·' and reported fourteen
// healthy files: latin1 cannot represent U+00B7 either, so the search matched the CORRECT UTF-8 for
// `·` (C2 B7). **A check written in terms of decoded characters cannot be trusted about bytes that
// fail to decode** — which is why this one is written in hex, and why it verifies by scanning again.
//
// Usage: node tools/repair-encoding.mjs <file> [file ...]

import { readFileSync, writeFileSync } from 'node:fs';

/** Damaged bytes -> the character they were meant to be. Order matters: the longer run first. */
const REPAIRS = [
  { from: Buffer.from('c3a2e282ace2809d', 'hex'), to: '—', was: '—' },
  { from: Buffer.from('c3a2e282acc2a6', 'hex'), to: '…', was: '…' },
  // The section sign, which is the most common character in this repository's prose after the em dash
  // — every ADR reference with a decision number in it has one. Its damage is a SHORTER run than the
  // other two because § is two bytes rather than three, so it needs its own entry.
  { from: Buffer.from('c382c2a7', 'hex'), to: '§', was: '§' },
];

const files = process.argv.slice(2);

if (files.length === 0) {
  console.error('usage: node tools/repair-encoding.mjs <file> [file ...]');
  process.exit(2);
}

let repaired = 0;
let clean = 0;
let refused = 0;

for (const path of files) {
  const original = readFileSync(path);
  let next = original;
  const found = [];

  for (const { from, to, was } of REPAIRS) {
    let at = next.indexOf(from);

    while (at >= 0) {
      found.push(was);
      next = Buffer.concat([next.subarray(0, at), Buffer.from(to, 'utf8'), next.subarray(at + from.length)]);
      at = next.indexOf(from, at + 1);
    }
  }

  if (found.length === 0) {
    console.log(`  clean       ${path}`);
    clean++;
    continue;
  }

  // Verified before writing, and by the same scan that found it. A repair that cannot be shown to
  // have removed the damage is a repair that has not been tested.
  let reason = null;

  try {
    new TextDecoder('utf-8', { fatal: true }).decode(next);
  } catch (error) {
    reason = `the result is not valid UTF-8 (${error.message})`;
  }

  if (reason === null) {
    const left = REPAIRS.filter(({ from }) => next.includes(from));

    if (left.length > 0) {
      reason = `damage survived: ${left.map((r) => r.was).join(', ')}`;
    }
  }

  if (reason === null) {
    // The repair must shorten the file and remove bytes, never add any. A round trip that grows is a
    // round trip that transformed something it was not asked to touch.
    if (next.length > original.length) {
      reason = `it would grow the file (${original.length} -> ${next.length} bytes)`;
    }
  }

  if (reason !== null) {
    console.log(`  REFUSED     ${path}: ${reason}`);
    refused++;
    continue;
  }

  writeFileSync(path, next);
  console.log(`  repaired    ${path}  ${found.length}x  (${[...new Set(found)].join(', ')})  -${original.length - next.length} bytes`);
  repaired++;
}

console.log(`\n  ${repaired} repaired, ${clean} already clean, ${refused} refused`);
process.exit(refused > 0 ? 1 : 0);
