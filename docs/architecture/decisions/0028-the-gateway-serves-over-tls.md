# ADR-0028 — The Gateway serves over TLS, and refuses to serve in the clear by accident

**Status:** Accepted
**Date:** 2026-10-07

## Context

**The Gateway serves plain HTTP, and nothing in this repository had ever said so.** Found on
2026-10-07 while answering a question about what stands between this project and a real deployment:
`deploy/docker-compose.yml` publishes `${SCADA_HTTP_PORT:-8080}:8080`, the Gateway listens on HTTP, and
no ADR, architecture document or deployment guide mentions TLS for the web interface at all.

So every sign-in sends a password in the clear, and every request afterwards carries the session token
ADR-0011 defines — an opaque bearer token, which is exactly the kind of credential that is worth
stealing and trivial to steal from an unencrypted connection. Anyone on the path has an Operator's
session, and ADR-0026 made an Operator someone who can **write to a plant**.

The gap is sharper for being surrounded by care. The edge link is TLS with a certificate per edge
(ADR-0017), the broker's ACL confines each edge to its own topics, the database connection is an
unprivileged role that cannot rewrite the audit trail (ADR-0011, ADR-0012). The one surface a **person**
touches had nothing.

**Why it was missed is worth recording**, because it is a shape that will recur: every phase's gate was
walked on `localhost`, where plain HTTP is unremarkable and a browser says nothing about it. A property
that only matters away from the machine it is tested on is one no amount of local walking will find.

## Decision

**1. The Gateway terminates TLS itself, from a certificate and key the deployment gives it.**

Not a reverse proxy in front of it. Three reasons, in order of weight:

- **It keeps the same origin.** The Gateway serves the built Angular client from its own origin, which
  is why there is no CORS allowance anywhere and no Gateway URL compiled into the client (Phase 6).
  A proxy can preserve that, and a proxy misconfigured cannot — and the failure is a client that
  half-works.
- **It adds no dependency.** Kestrel already has this; a proxy is a new image, a new configuration
  language and a new thing to version, which ADR-0006 would have to be amended for.
- **It does not prevent a proxy.** A deployment that wants one — for a shared certificate, for several
  services on one name — still can, and decision 2 is how it says so.

**2. A deployment that terminates TLS somewhere else must say so, and the Gateway says it back.**

`Server:TlsTerminatedUpstream: true` means *something in front of me is doing this*. The Gateway then
serves HTTP, and **logs at startup that it is doing so because it was told to**, naming the setting. A
deployment's security posture must be legible from its own logs: a later reader should not have to
infer from an absence.

**3. With neither a certificate nor that declaration, the Gateway refuses to start, and names both ways
out.**

The same shape as ADR-0012's schema refusal, and for the same reason: **the dangerous configuration is
the one nobody chose.** A Gateway that quietly fell back to HTTP would be secure only for as long as
whoever deployed it happened to remember, and the first person to find out otherwise would be whoever
read the password off the wire.

The refusal is a message, not a stack trace — it says what to set, both options, and is caught in
`Program` beside the others.

**4. Development is an explicit exception, not a silent one.**

`appsettings.Development.json` carries `TlsTerminatedUpstream: true`, so `dotnet run` and the client's
dev-server proxy keep working. It is the *same* setting a production deployment behind a proxy uses,
which means there is no development-only code path and nothing that exists to be forgotten. What makes
it safe is that the shipped image does not run in Development.

**5. Serving TLS also redirects HTTP to HTTPS. It does not enable HSTS by default.**

The redirect is the courtesy — an operator typing a bare host name should arrive, not fail.

**HSTS is refused as a default and offered as `Server:Hsts: true`**, which is the opposite of the usual
advice and is deliberate. HSTS is remembered by the browser and **cannot be overridden by the person
using it**. A plant whose certificate is self-signed or signed by an internal CA — which is most of
them — would hand its operators a browser that refuses the screen with no way past, during an incident,
on the one machine that matters. A deployment with a publicly trusted certificate should turn it on;
one that cannot should not be given it silently.

**6. The certificate is the deployment's, and this project does not manufacture it.**

`deploy/cloud/certs.sh` makes certificates for the broker and the edges because those are a closed
system: both ends are ours and the trust decision is ours. A browser's trust is not ours — it belongs
to the organisation's CA, or to a public one. The guide says how to point the Gateway at a certificate
and what the choices are; it does not generate one and pretend that is enough.

## Consequences

- **A deployment can no longer be insecure by accident.** It can still be insecure on purpose, in one
  named setting, which is the distinction worth preserving.

- **An upgrade of an existing deployment will stop.** That is the intended behaviour and the guide says
  so: a Gateway that has been serving HTTP will refuse the new image until its operator has either
  given it a certificate or declared a proxy. **This is the only breaking change in the project so
  far**, and it is one where continuing quietly is worse than stopping loudly.

- **The cloud topology inherits it**, and matters more: `deploy/cloud` is the deployment most likely to
  be reachable from somewhere other than a plant floor.

- **Still not decided here:** client certificates for operators, whether the session token should also
  be scoped to a connection, and certificate renewal. The first two are ADR-0011's territory and
  neither is urgent while the token is short-lived; renewal is a deployment practice that the guide
  can carry without a decision.

- **What this does not fix:** everything behind the Gateway is unchanged. The database connection, the
  broker link and the audit trail were already covered; this closes the one surface that was not.

## Verified in review by

- A Gateway started with neither setting refuses, and the message names both ways out.
- A Gateway given a certificate serves HTTPS, and an HTTP request to it is redirected.
- A Gateway told TLS is terminated upstream serves HTTP **and says so in its startup log**.
- HSTS is absent unless asked for, which is checked by its absence from the response headers.
