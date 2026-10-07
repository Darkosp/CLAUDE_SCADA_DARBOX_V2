// A backtick inside a component's `template:` or `styles:` block ends that block.
//
// **This caught two people on one day — both of them me — and `npm test` cannot see it.** The client's
// test script compiles three files (`models.ts`, `tag.ts`, `screen.ts`); a component is only parsed by
// `ng build`. And the failure does not point at the backtick: TypeScript keeps parsing, the rest of the
// file becomes string and expression in the wrong order, and what comes out is
// `NG1002: Incorrect number of arguments to @Component decorator` several hundred lines later.
//
// On 2026-10-06 it was a comment in `screen-editor.ts` naming a file in backticks. On 2026-10-07 it
// was a comment in `symbol.ts` quoting a plus sign and a state name. Both were documentation, which is
// the whole trap: prose about code naturally reaches for backticks, and inside these two blocks they
// are syntax.
//
// So: no backtick inside a `template:` or `styles:` literal, anywhere in the client. The rule is
// crude on purpose — a real parser would be the right tool and a wrong dependency — and the cost of
// obeying it is writing one comment without quote marks.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';

const appDir = new URL('../src/app/', import.meta.url);
const sources = readdirSync(appDir).filter((name) => name.endsWith('.ts'));

/**
 * The spans of every `template:` / `styles:` literal in a component, by where they start.
 *
 * Found by walking forward from the opening backtick to the next unescaped one, which is exactly what
 * the TypeScript lexer does — and is why a third backtick in between is the defect rather than a
 * nesting.
 */
function literalsIn(source) {
  const spans = [];

  for (const key of ['template: `', 'styles: `']) {
    let at = source.indexOf(key);

    while (at !== -1) {
      const start = at + key.length;
      const end = source.indexOf('`', start);
      spans.push({ key: key.slice(0, -3), start, end, body: source.slice(start, end === -1 ? undefined : end) });
      at = source.indexOf(key, end === -1 ? source.length : end);
    }
  }

  return spans;
}

for (const name of sources) {
  const source = readFileSync(new URL(name, appDir), 'utf8');
  const spans = literalsIn(source);

  if (spans.length === 0) {
    continue;
  }

  test(`${name}: no template or styles block is ended early by a backtick`, () => {
    for (const span of spans) {
      assert.notEqual(span.end, -1, `${name}: the ${span.key} block is never closed`);

      // The body is everything up to the FIRST closing backtick. If the block's real content runs
      // past it, the giveaway is that the character after it is not the comma or newline that closes
      // a decorator property — it is more markup or more CSS.
      const after = source.slice(span.end + 1, span.end + 40).trim();

      assert.ok(
        after.startsWith(',') || after.startsWith('}') || after === '',
        `${name}: the ${span.key} block ends at a backtick that is inside it, not at its end. `
          + `What follows is ${JSON.stringify(after.slice(0, 30))}. A backtick in a comment or a `
          + `string inside these blocks is syntax: remove it, and say the same thing without it.`,
      );
    }
  });
}

test('the scan found the component files it is supposed to be guarding', () => {
  // The control. A regex that matched nothing would make every test above pass in silence, which is
  // the shape of test this project has already been caught by once.
  const guarded = sources.filter((name) => literalsIn(readFileSync(new URL(name, appDir), 'utf8')).length > 0);

  assert.ok(
    guarded.length >= 4,
    `only ${guarded.length} component file(s) were scanned: ${guarded.join(', ')}`,
  );
  assert.ok(guarded.includes('symbol.ts'), 'symbol.ts is where the 2026-10-07 instance was');
  assert.ok(guarded.includes('screen-editor.ts'), 'screen-editor.ts is where the 2026-10-06 instance was');
});
