# ADR-0031 — The Gateway hardens the surface a person signs in through

**Status:** Accepted
**Date:** 2026-10-07

## Context

**The standards audit found the largest security gap in the product is not a protocol feature but a missing
control.** `docs/architecture/standards-baseline.md` §3 records it plainly: a password is checked, and
**nothing limits how many times it may be guessed**. There is no lockout, no delay, no rate limit, anywhere
in the sign-in path.

**And the care around it makes the absence sharper.** `Authenticator.LoginAsync` verifies a decoy hash when
the name matches nobody, so that an unknown name takes as long to refuse as a wrong password — an explicit
defence whose comment says why. A twelve-character minimum is enforced. Sessions are opaque and
server-side (ADR-0011) and the whole interface is behind TLS (ADR-0028). Every one of those controls
protects the *quality* of a single guess, and none of them limits the *number* of them.

**What the standards say, stated as carefully as the audit stated it.** IEC 62443-4-2 names limiting
repeated failed authentication attempts among the account-management and authenticator requirements of a
component, and NIST SP 800-82 hands the subject to the access-control family of SP 800-53 for the
organisation to apply. **No requirement numbers are quoted here, and that is deliberate**: the audit's §10
records that every standard it assessed is paywalled and that none was read in full, so a number in this
ADR would be a citation nobody in this repository can check.

## Decision

**1. A failed sign-in is counted against the account, and enough of them shut it for a window.**

Five consecutive failures lock the account for fifteen minutes by default. The count lives on the user row
(`failed_sign_ins`, `locked_until`), so it survives a Gateway restart — an attacker's cheapest move against
an in-memory counter is to make the process restart.

**2. The window is a comparison, not a job.**

`locked_until` in the past means the account is not locked. No timer, no sweeper, no scheduled task that can
fail to run: a lock that depends on something executing is a lock that is open whenever that thing is not.
**And an expired lock starts the count again** — the first failure after a lock has elapsed is the first of
a new series, not the sixth of an old one, because a person who was locked out last week and mistypes once
today has not earned a second lock.

**3. A successful sign-in clears the count.** Four mistyped passwords and then the right one is a person
having a bad morning, not an account one typo from a lock.

**4. The locked account is told, and this is a deliberate oracle.**

Five failures answer `423 Locked` and name the moment it reopens. That tells an attacker the name exists,
which this project refuses to do elsewhere — a Site a caller cannot see answers 404 rather than 403
(ADR-0011) precisely so that ids cannot be enumerated. **The trade is made the other way here, and the
reason is the person at three o'clock in the morning**: an operator locked out of a plant console who is
told "wrong user name or password" will go looking for a password problem they do not have, on the one
screen where the answer has to be immediate. The cost is bounded — the name was going to be discoverable by
asking a colleague, and what the message reveals is that an account is locked, not that the guess was any
good.

**5. An Admin's password reset clears the lock, and no new endpoint is added.** The way out of a lock is the
one that already exists for a forgotten password. An "unlock" button would be a second path to the same
state, and this project has already refused one of those (ADR-0026's dropped screen-write switch) for the
same reason.

**6. The numbers are configuration with defaults, and nonsense is refused by name.** A deployment may want
three attempts or thirty minutes; `Security:Lockout:Attempts` and `Security:Lockout:Minutes` carry it, and a
value outside what the check can mean is refused at startup with the value and the range in the message —
the pattern §2.0f set for an out-of-range response timeout and ADR-0029 §3 followed for `points`. **Refused
rather than clamped**, because a lockout silently shortened is a lockout the operator believes is there.

**7. Per-caller limiting is *not* this decision, and the reason is not budget.** Counting failures by the
address a request arrives from is a different control against a different attack — one guess against a
thousand accounts. It cannot be built honestly until the question *what is the caller's address* is
answered: ADR-0028 §2 allows something in front of the Gateway to terminate TLS, and behind such a thing
`RemoteIpAddress` is the proxy. Trusting a forwarded header without deciding which hops may set it is worse
than having no per-caller limit, because it converts a missing control into a bypassable one that looks
present. Recorded open in `open-work.md` §3.

**8. Response headers are set, and the inline theme script is allowed by hash rather than by
`'unsafe-inline'`.**

The Gateway sends `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`,
`X-Frame-Options: DENY`, `Permissions-Policy` closing the device APIs a plant console has no use for, and a
`Content-Security-Policy` of `frame-ancestors 'none'; object-src 'none'; base-uri 'self'; script-src …`.
What that buys is real and bounded: the interface cannot be framed by another site, a response cannot be
sniffed into executing, and no referrer leaks out of a plant.

**`script-src` was left out when this ADR was written, and it is the one thing here that has since been
corrected.** The reason it was left out was real — `index.html` boots the stored theme with an inline script
so the dark theme applies before the first paint (ADR-0027), so `script-src 'self'` alone would break it, and
`'unsafe-inline'` is a directive that does not do the thing it is named for. **The answer turned out to be
smaller than "a separate slice"**: at startup the Gateway reads the `index.html` it is about to serve and
puts the SHA-256 of each inline script's own text into the header (`ScriptHashes`). Two details are the whole
of the correctness — **the hash covers the element's text and not its tags**, because hashing the element
produces a header that looks right and a script the browser refuses; and **a `<script src>` gets no hash**,
because `'self'` is what loads it and a hash of content the browser ignores allows nothing. With no built
client in the web root — a developer running `ng serve` — the source is the bare `'self'`, which is then
exactly true rather than a hash nobody can satisfy.

The hash is computed once, at startup: the file changes only with an upgrade, and an upgrade restarts the
Gateway. **A deployment that replaced the client without restarting the Gateway would leave the header
describing the previous build**, and that is written here rather than left to be discovered as a theme that
stops applying.

**9. It is journalled.** The transition into a lock appends `auth.account_locked` with the user name, beside
the `auth.login_failed` rows that already exist (ADR-0013's append-only journal). **Reading the trail was the
audit's other cheap gap and is no longer open**: ADR-0032 decides it and the same pull request builds it.

## Consequences

- **One migration**: two columns on `app_user`, a CHECK that the count cannot be negative, and
  `app_user_active` dropped and recreated to carry them — the same cascade 0007 and 0020 document, with the
  view's `SELECT` granted explicitly because granting on a table does not grant on the view over it.
- **An upgrade is invisible.** Every existing row gets zero failures and no deadline, which is an unlocked
  account, so nothing changes for anybody until somebody fails five times.
- **A failed sign-in now costs a write.** One `UPDATE` per wrong password, on a path an attacker can reach
  without a session. It is a small, indexed, single-row write, and the alternative — a counter in memory —
  is the control an attacker restarts the process to clear.
- **A locked operator has a documented way out**, and it is the password reset an Admin already performs.
- **What this does not do**: it does not stop an attacker from trying one password against many accounts,
  it does not make a weak password strong (the twelve-character minimum is the whole of that control), and
  it does not touch the OPC UA driver's `useSecurity: false` — the audit's second-biggest gap, which needs
  its own decision about certificates in a plant.

## Verified in review by

- Five wrong passwords lock the account; the sixth attempt with the **right** password is refused, and the
  message names when it reopens.
- After the window has elapsed the account works again, and **the first failure after it starts a fresh
  count** rather than re-locking immediately.
- A successful sign-in clears the count: four failures then a success, then four more, does not lock.
- An Admin's password reset clears both the count and the lock.
- A lock survives a Gateway restart, because it is a row and not a field in a process.
- `Security:Lockout:Attempts` or `:Minutes` set to a value the check cannot mean is refused at startup with
  the value and the range named.
- Every response carries the five headers, and the client still boots its stored theme — the second is what
  proves the inline script is allowed by the right hash rather than by `'unsafe-inline'`.
- **The hash is of the script's text and not of its tags**, and a `<script src>` gets none. Both are paths
  where the header still looks correct and the browser refuses the script, so both have a test whose
  expected digest is computed from the script's own text rather than copied from a run.
