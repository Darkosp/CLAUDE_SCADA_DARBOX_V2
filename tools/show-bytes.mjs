// Shows the exact bytes of a damaged run, so a repair can be written against what is there rather
// than against what it is assumed to be. Written after two repairs were guessed at and refused.

import { readFileSync } from 'node:fs';

const [path, needle] = process.argv.slice(2);
const bytes = readFileSync(path);

const at = bytes.indexOf(Buffer.from(needle, 'latin1'));

if (at < 0) {
  console.error(`"${needle}" not found in ${path}`);
  process.exit(1);
}

const end = Math.min(bytes.length, at + needle.length + 24);
const window = bytes.subarray(Math.max(0, at - 8), end);

console.log(`  ${path}, "${needle}" at byte ${at}`);
console.log(`  hex   ${[...window].map((b) => b.toString(16).padStart(2, '0')).join(' ')}`);
console.log(`  utf8  ${JSON.stringify(window.toString('utf8'))}`);
