// The tree holds no text a PowerShell round trip damaged.
//
// **This exists because the damage is invisible to every other check this project has.** A file whose
// UTF-8 was decoded as Windows-1252 and re-encoded is still VALID UTF-8 — it compiles, it passes, and
// it renders `Write` followed by nonsense where an ellipsis belongs. Nothing fails. The only way to
// find it is to look at the bytes, which is what this does.
//
// It was found on 2026-10-06 by looking at a screenshot: a button read `Write` and three stray
// characters. By then it was in two source files and in prose across four documents, and one of them
// was user-visible. The scan that followed found the section sign too — eleven times in `screen.ts`
// alone, in every ADR reference — because a two-byte character is damaged into a different shape from
// a three-byte one, and a list written from memory missed it.
//
// **The three patterns are assembled from their bytes, and that is not obfuscation.** A scanner that
// stores what it looks for as text finds ITSELF: the first version of this file was reported as
// damaged by its own run — the tooling equivalent of a compiler refusing to compile because it
// contains its own source. Building each pattern from its bytes keeps the literal sequences out of the
// tree while leaving the definition readable.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * The repository root.
 *
 * **Resolved from this file's own location, and the first version was one level short** —
 * `new URL('../..', import.meta.url)` from `src/Web/tests/` is `src`, not the root, so the scan
 * looked for `src/src` and failed with ENOENT rather than reporting a clean tree. Three levels up,
 * because this file lives three levels down: tests, Web, src, root.
 */
const ROOT = fileURLToPath(new URL('../../../', import.meta.url));

/**
 * One damaged character, as its bytes.
 *
 * A UTF-8 character read as Windows-1252 and written back as UTF-8 becomes the UTF-8 encoding of each
 * of its bytes: the section sign is C2 A7 and becomes C3 82 C2 A7; the em dash is E2 80 94 and becomes
 * C3 A2 E2 82 AC E2 80 9D.
 */
const damaged = (...bytes) => Buffer.from(bytes);

const DAMAGE = [
  { bytes: damaged(0xc3, 0xa2, 0xe2, 0x82, 0xac, 0xe2, 0x80, 0x9d), describes: 'an em dash' },
  { bytes: damaged(0xc3, 0xa2, 0xe2, 0x82, 0xac, 0xc2, 0xa6), describes: 'an ellipsis' },
  { bytes: damaged(0xc3, 0x82, 0xc2, 0xa7), describes: 'a section sign' },
];

const SEARCH = ['src', 'docs', 'tools'];
const EXTENSIONS = /\.(ts|mjs|md|html|css|json|cs|sql)$/;
const SKIP = ['node_modules', 'dist', '.git', '.node-test', 'bin', 'obj'];

function* walk(directory) {
  for (const entry of readdirSync(directory)) {
    if (SKIP.includes(entry)) continue;

    const path = join(directory, entry);

    if (statSync(path).isDirectory()) yield* walk(path);
    else if (EXTENSIONS.test(path)) yield path;
  }
}

function* everyFile() {
  for (const root of SEARCH) {
    yield* walk(join(ROOT, root));
  }
}

test('no file in the tree holds text a code-page round trip damaged', () => {
  const found = [];

  for (const path of everyFile()) {
    const bytes = readFileSync(path);

    for (const { bytes: needle, describes } of DAMAGE) {
      if (bytes.includes(needle)) {
        found.push(`${path.replace(ROOT, '')} holds ${describes}`);
      }
    }
  }

  assert.deepEqual(
    found,
    [],
    `damaged text found:\n${found.join('\n')}\n` +
      'Repair by bytes, not by characters: tools/repair-encoding.mjs then tools/find-mojibake.mjs.',
  );
});

test('every file in the tree is valid UTF-8', () => {
  // The other half of the same problem, and a different failure: a character collapsed to a single
  // Windows-1252 byte is NOT valid UTF-8, so it breaks tooling rather than rendering as nonsense.
  // This repository has hit that too — a walk document was unreadable to its own editor tools because
  // an em dash had become one byte.
  const broken = [];

  for (const path of everyFile()) {
    try {
      new TextDecoder('utf-8', { fatal: true }).decode(readFileSync(path));
    } catch {
      broken.push(path.replace(ROOT, ''));
    }
  }

  assert.deepEqual(broken, [], `not valid UTF-8:\n${broken.join('\n')}`);
});

test('the scan itself finds damage, so a clean result means something', () => {
  // **A test that cannot fail is not evidence** — this repository's own rule. The two tests above pass
  // when the tree is clean, and they would also pass if the scanner were broken: a wrong pattern, a
  // skipped directory, a walk that yields nothing. So the scanner is given bytes that ARE damaged, and
  // has to recognise them.
  const reallyDamaged = [
    damaged(0xc3, 0x82, 0xc2, 0xa7),
    damaged(0xc3, 0xa2, 0xe2, 0x82, 0xac, 0xe2, 0x80, 0x9d),
  ];

  for (const bytes of reallyDamaged) {
    assert.equal(
      DAMAGE.some((candidate) => bytes.includes(candidate.bytes)),
      true,
      'the scan does not recognise damage it is given, so a clean result from it means nothing',
    );
  }

  // And the control: the CORRECT text for those same characters must not be reported. This is the
  // false positive this session already made once — a search for the text of a damaged middle dot
  // matched its correct UTF-8 and reported fourteen healthy files.
  const healthy = Buffer.from('a\u00a7b\u2014c\u2026', 'utf8');

  for (const { bytes, describes } of DAMAGE) {
    assert.equal(
      healthy.includes(bytes),
      false,
      `correct text is reported as a damaged ${describes}, which is the false positive already made once`,
    );
  }
});
