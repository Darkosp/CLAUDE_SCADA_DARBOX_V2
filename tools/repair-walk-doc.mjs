// Repairs docs/roadmap/walk-2026-10-06.md, whose appended half a PowerShell here-string wrote as
// cp1252 — leaving seven bytes that are not valid UTF-8, and a file the editor tools cannot read in
// order to fix.
//
// **The repair is by byte, and the first two attempts at this were wrong in a way worth recording.**
// The bad bytes are an em dash and an ellipsis, each collapsed to the single byte its Windows-1252
// code point has. Reading the whole file as latin1 and writing it back as latin1 looked equivalent
// and was not: latin1 cannot carry a character above U+00FF, so the repaired em dash came back out as
// a question mark and everything else was silently re-encoded. Node writes the bytes it is given, so
// the fix is to replace the two byte sequences and write the buffer back untouched.
//
// Also corrects the write-walk paragraph: the edge-assignment behaviour was recorded as a defect
// before `SaveDeviceRequest` was read, and it is a documented contract instead.

import { readFileSync, writeFileSync } from 'node:fs';

const path = 'docs/roadmap/walk-2026-10-06.md';
let bytes = readFileSync(path);

/**
 * Replaces every occurrence of one byte sequence with another, at the byte level.
 *
 * Deliberately not a regex on decoded text: the whole difficulty is that this file does not decode.
 */
function replaceBytes(buffer, from, to) {
  const out = [];
  let at = 0;

  for (;;) {
    const found = buffer.indexOf(from, at);

    if (found < 0) {
      out.push(buffer.subarray(at));
      break;
    }

    out.push(buffer.subarray(at, found), to);
    at = found + from.length;
  }

  return Buffer.concat(out);
}

// Windows-1252 0x97 is an em dash and 0x85 an ellipsis. On their own, both are invalid UTF-8, which is
// how they were found — and each was written where its UTF-8 sequence belongs.
bytes = replaceBytes(bytes, Buffer.from([0x97]), Buffer.from('—', 'utf8'));
bytes = replaceBytes(bytes, Buffer.from([0x85]), Buffer.from('…', 'utf8'));

// Now it decodes, and the correction can be made as text.
let text = bytes.toString('utf8');

// **The file is CRLF and the paragraph below is written with LF.** The first run of this reported
// "not found" and wrote nothing, which is the honest outcome and better than a blind replace.
const crlf = text.includes('\r\n');
if (crlf) {
  text = text.replaceAll('\r\n', '\n');
}

const before = `**Two things the walk found, and the first is worth more than the pass.**

1. **Updating a device through the API clears its edge assignment unless \`edgeId\` is sent again.** The
   first write attempt answered **HTTP 502 after 4.75 s** with \`The device did not accept the write:
   Name or service not known\`. That was not the link failing: the assignment had been dropped by the
   device update, so the cloud tried to reach \`modbus-sim:5502\` **directly**, could not resolve a name
   that exists only inside another network, and reported the device's refusal. **The refusal was
   correct and the latency was correct** — it is the honest answer to "the cloud cannot reach this
   device". What was wrong was the assignment, and the API accepted the update that caused it without
   saying that an edge assignment was being dropped. **Full replacement semantics on a device that
   carries an assignment is a defect worth fixing**: an operator editing a hostname should not
   silently detach a device from its edge.`;

const after = `**Two things worth recording, and the first was a near-miss rather than a finding.**

1. **A device update that omits \`edgeId\` releases the device from its edge, and that is the documented
   contract rather than a defect.** The first write attempt answered **HTTP 502 after 4.75 s** with
   \`The device did not accept the write: Name or service not known\`. The link had not failed: the
   assignment was gone, so the cloud tried to reach \`modbus-sim:5502\` **directly**, could not resolve a
   name that exists only inside another network, and reported the device's refusal. **The refusal was
   correct and the 4.75 s was correct** — it is the honest answer to "the cloud cannot reach this
   device", and it is what the write path is supposed to say.

   I called it a defect first, and reading \`SaveDeviceRequest\` refused that: \`EdgeId\` is a deliberate
   full replacement, because ADR-0019 says *"assigning and releasing are ordinary edits of the device,
   and this is the only way either happens."* So the API behaved as designed. **What the walk did
   surface is that the design makes this easy to do by accident** — a \`PUT\` that changes one field has
   to resend every other, and the only signal that an assignment was dropped is a write failing four
   seconds later with a message about DNS. That is worth a second look, and it is a question about the
   API's shape rather than a bug in it.`;

if (!text.includes(before)) {
  console.error('the paragraph to correct was not found — the encoding repair is written, the correction is not');
  writeFileSync(path, Buffer.from(crlf ? text.replaceAll('\n', '\r\n') : text, 'utf8'));
  process.exit(1);
}

text = text.replace(before, after);

writeFileSync(path, Buffer.from(crlf ? text.replaceAll('\n', '\r\n') : text, 'utf8'));

// Prove it, rather than assume it: this is the check that failed twice.
const written = readFileSync(path);
try {
  new TextDecoder('utf-8', { fatal: true }).decode(written);
  console.log(`  ${path}: ${written.length} bytes, valid UTF-8`);
} catch (error) {
  console.error(`  STILL INVALID: ${error.message}`);
  process.exit(1);
}
