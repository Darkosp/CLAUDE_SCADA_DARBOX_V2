# ADR-0033 — The OPC UA driver negotiates security, and an unsecured session has to be asked for

**Status:** Accepted
**Date:** 2026-10-08

## Context

`Drivers.OpcUa` connects with `useSecurity: false` and `new UserIdentity(new AnonymousIdentityToken())`
(`OpcUaDriver.cs:80-101`). Every value this product reads from an OPC UA server therefore crosses the
plant network **unencrypted and unauthenticated**, and whatever answers on that address is trusted as
far as the session is concerned.

**The deferral was never recorded anywhere.** The comment on those lines says security "belongs with
the auth work in Phase 5". Phase 5 shipped on 2026-09-11 — sessions, roles, the audit trail — and did
not pick it up, because nothing pointed at it. `standards-baseline.md` §2.1 found it on 2026-10-07 and
called it *"the largest undocumented gap this audit found"*; that document records the gap, and a
baseline is a description, not a decision. This ADR is the decision.

**It is the last hop in the clear.** The edge link is TLS with a certificate per edge (ADR-0017), the
broker's ACL confines each edge to its own topics, the database connection is an unprivileged role that
cannot rewrite the audit trail (ADR-0011, ADR-0012), and the Gateway now serves the interface over TLS
and refuses to serve it in the clear by accident (ADR-0028). The one remaining unencrypted hop is the
one carrying **plant measurements**, and it is the hop closest to the plant.

**What makes this worse than a plain gap: the machinery is already there and decides nothing.**
`BuildConfigurationAsync` builds a full `SecurityConfiguration` — own, trusted, issuer and rejected
certificate stores — and creates a self-signed client certificate on first run.
`OpcUaDriverFactory` already takes `acceptUntrustedCertificates` per device and defaults it to
**false**, with a comment arguing that trusting whatever answers on an address should be deliberate.
All of that is correct, and with `useSecurity: false` **none of it is consulted**: there is no channel
to secure and no server certificate to validate. A reader of this code — including the author of
ADR-0028, twice — sees a PKI and concludes the driver is secured. **Equipment that looks like a lock is
worse than no lock**, because it stops anybody asking.

**And the project has met this exact failure once already, four days ago.** The content security policy
of ADR-0031 was correct in source, shipped, and refused every inline script on the page for two days,
while every instrument short of opening the browser said it was fine
([`walk-2026-10-08.md`](../../roadmap/walk-2026-10-08.md) §4). The shape repeats here: a security
property that only matters against something this repository does not run. Decision 5 exists because of
it.

## Decision

**1. The driver selects the strongest endpoint the server offers, and a server that offers none is a
refusal rather than a silent downgrade.**

`useSecurity: true`. If the server's endpoint list contains nothing above `MessageSecurityMode.None`,
the connection **fails with a reason that says so** — it does not quietly fall back. A downgrade nobody
asked for is the defect this ADR exists to remove, and a downgrade that happens automatically on a bad
day is the same defect with worse timing.

**2. A deployment that really must talk to an unsecured server says so per device, and is told it did.**

The device's `security` connection setting defaults to `required`. `security: none` is the explicit
opt-out: the driver then connects exactly as it does today, and **logs at connect that it is doing so
because it was told to**, naming the device and the setting.

This is ADR-0028 decision 2 applied one layer down, and deliberately so — the same words, the same
default, the same obligation to say it out loud. An installation with a twenty-year-old server that
speaks no security is a real situation and this product must not make it unservable; what it must not
do is make it the **default**, or let it happen without anybody choosing it.

**3. The strongest the server offers is accepted even when it is a deprecated policy, and the policy is
named in the log.**

`Basic128Rsa15` and `Basic256` are deprecated by the OPC Foundation, and a plant full of equipment that
offers nothing better is not a hypothetical. **Refusing them would be a net loss**: an operator who
cannot connect at all does not go and buy a new server, they set `security: none`, and a deprecated
policy is enormously better than no policy. So the driver takes what it can get and **names the policy
it negotiated**, at connect, so that a weak one is visible to anybody who looks rather than
indistinguishable from a strong one.

**4. A secret never lives in `connection_settings`.**

Identity stays **anonymous** by default, and the client certificate created on first run is what
identifies the application to the server. If a named user is ever needed, the device's connection
setting **names a credential and does not carry one** — the secret reaches the Gateway the way the
database passwords already do (ADR-0012's split), never the configuration row.

The reason is concrete rather than principled. `ConnectionSettings` is a dictionary on the device row:
it is echoed back by the API, and a device edit writes it into `audit_log.detail` — which ADR-0032 §6
makes **opaque JSON that is never parsed**, so nothing in the reader could redact a field even in
principle. A password put there would be printed, in full, on a screen whose whole purpose is that an
Admin can read it. *(The walk of 2026-10-08 saw `audit_log.detail` print a whole `tag.write` payload,
six fields of which five were null. That is the mechanism, observed.)*

Username and password identity is **not built by this ADR** — nothing has asked for it. The rule is
decided now because the cheap wrong answer (*put it in `connection_settings`, it is just another
setting*) is the one a later session would reach for.

**5. The demo and the simulator run on the secured path, not on the opt-out.**

`tools/OpcUaSimulator` offers exactly one endpoint, `MessageSecurityMode.None` with
`SecurityPolicies.None`. Left alone, the demo device would have to declare `security: none` — and then
**nothing in this repository would ever exercise the path that is now the default**, which is precisely
how `useSecurity: false` survived two phases and an ADR of its own.

So the simulator gains a secured endpoint and the demo device uses it. **The default must be the path
that is walked.** The `none` opt-out is then the thing that needs declaring, which is the right way
round.

**6. A refused certificate says what it is, and is not confused with a device being offline.**

With security on, `AutoAcceptUntrustedCertificates` finally decides something: a server whose
certificate is not in the trusted store is refused, and the certificate lands in `pki/rejected`.

**Nobody looks in a directory.** So the refusal must reach the two places a person is already looking:

- the tag's Bad reason names it — ADR-0003's rule that a tag with no value says *why*, which both
  drivers were taught on 2026-10-02;
- and it is **distinguishable from an unreachable device**, because the two remedies have nothing in
  common. One is *go and trust a certificate*; the other is *go and find out why the machine is not
  answering*. A reader given one message for both will try the wrong one first, every time.

The message carries the certificate's **thumbprint and subject**, because those are what an operator
compares against the server before deciding to trust it.

**7. The client certificate survives a restart.**

It is written under `pki/own` in the Gateway's working directory, which the deployment already keeps on
the `gateway-pki` volume. A certificate regenerated on every restart would have to be re-trusted by the
server on every restart, which would teach every operator to switch `acceptUntrustedCertificates` on
and leave it on — a default defeated by operational friction rather than by argument.

## Consequences

- **An existing OPC UA deployment stops connecting until somebody decides.** That is the point, and it
  is the same consequence ADR-0028 accepted: the refusal is a question being asked once, out loud,
  rather than an answer being assumed forever. The failure names the setting and both of its values.
- **The driver gains a reason to fail that is not a network failure**, and ADR-0003's silence rule is
  what carries it to the screen. The drivers already name why a tag has no value; this adds causes to
  that vocabulary rather than a new mechanism.
- **The edge agent gets this too, and for free** — `Drivers.OpcUa` is one module composed in two places
  (ADR-0002), so an edge reading a plant over OPC UA negotiates the same way. The edge's own uplink was
  already TLS (ADR-0017); this closes the hop on the other side of it.
- **`acceptUntrustedCertificates` becomes load-bearing.** It has existed and decided nothing since
  Phase 4. Any deployment that set it to `true` believing it was doing something was mistaken, and from
  now on will be right.
- **The audit trail covers the human decision already**: switching `acceptUntrustedCertificates` or
  `security` on a device is a device edit, and device edits are audited. No new audit action is added —
  a certificate being rejected is an event, not a person's action, and ADR-0032's trail is of actions.
- **ISA-18.2 priority and the other standards gaps are untouched.** This closes one row of
  `standards-baseline.md` §2.1 and no other; that document is updated in the same pull request as the
  implementation, not here.

## Alternatives considered

**Pin a specific security policy per device.** Refused for now: *the strongest the server offers* is a
safe rule that needs no knowledge, and a pin is a setting that must be kept correct as servers are
upgraded. If a deployment ever needs to forbid a particular policy, that is a new setting with a reason
behind it, not a default.

**Refuse deprecated policies.** Refused, with decision 3's reasoning: it would push installations onto
`security: none`, which is strictly worse. Visibility instead of refusal.

**Build username and password identity now.** Refused: nothing has asked for it, and building an
unneeded credential path means deciding where a secret lives before there is a secret. Decision 4
settles the rule so that the decision is already made when the need arrives.

**Leave the simulator unsecured and let the demo declare `security: none`.** Refused — decision 5. It
would ship the new default untested by anything, which is the failure mode this ADR is cleaning up
after.

**Record the deferral instead of closing it.** This was genuinely open: an ADR saying *"OPC UA security
is deferred, here is why"* would at least have removed the undocumented part. Refused because the honest
version of that ADR has no *why* left in it. The original reason — "it belongs with Phase 5" — expired
when Phase 5 shipped without it, and nothing has replaced it except the work not having been done.

## Verified in review by

- A server offering only `None` is refused by default, with a message naming the `security` setting and
  both of its values — and **the same server connects** when the device declares `security: none`.
- The opt-out is logged at connect, naming the device.
- The negotiated policy is named in the log on a successful secured connect.
- A server whose certificate is untrusted is refused, and the tag's Bad reason **names the certificate
  problem and its thumbprint** rather than reporting the device as unreachable; a test asserts the two
  reasons differ.
- `acceptUntrustedCertificates: true` on that device connects.
- The simulator offers a `SignAndEncrypt` endpoint, and the demo device reaches it **without** declaring
  an opt-out.
- No connection setting on any device carries a secret, and a test over the settings a device may hold
  fails if one is added.
- The client certificate is unchanged across a Gateway restart.
- **Walked, not only tested**: a real connect against the simulator with security on, the device edited
  to the opt-out and back, and the certificate store emptied to see what an operator is actually shown.
  ADR-0028 and ADR-0031 both shipped correct and broken; this one is not believed until it is seen.
