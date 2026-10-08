# Standards baseline — which standards a serious SCADA is expected to meet, and where this repository stands

**Written 2026-10-07.** The owner asked, in his own words: *which and how many standards are needed for a
serious SCADA system, and are they all implemented so far — check and tell me where we stand.*

## What this document is not

It is a **register**, not a source of truth. The binding decisions are the
[ADRs](decisions/README.md); the phase scope is [`phase-plan.md`](../roadmap/phase-plan.md); what is
unfinished is [`open-work.md`](../roadmap/open-work.md). Where this document and one of those disagree,
they win and this file is wrong.

It is also **not a claim of conformity to anything**. This product has been certified against no standard,
has never been assessed by a third party, and nothing here should be quoted as evidence that it has.
What follows is an internal reading of the standards that plausibly apply, against the code as it stands.

**How it was checked, and what that is worth.** Every entry was verified by reading this repository —
the ADRs listed in [`decisions/README.md`](decisions/README.md) (0029 is the last entry there),
`phase-0-architecture.md`, `phase-plan.md`, `open-work.md`, and the code and tests named in each entry —
and by reading a public source for the standard itself, cited at the end of the entry. **I did not read
the standards themselves**: almost all of them are paywalled, and where a clause number or a
sub-requirement list would have had to be invented, this document names the requirement in words instead
and says that it is unverified. **A wrong clause number is worse than no clause number**, because it
sends a reader to the wrong page of a document they then have to buy.

## How to read the status column

| Status | What it means |
|---|---|
| **Implemented** | The parts of the standard that apply to a product of this kind exist in code, and a test fails if they stop working. Any remaining gap is named. |
| **Partial** | Some of what the standard asks of this product exists, with the rest named concretely. |
| **Not implemented** | Nothing of it exists. |
| **Out of scope by decision** | This product deliberately is not in the standard's subject. The refusal is recorded, with its reason, as loudly as a feature would be. |
| **Deployment practice** | The standard binds the plant's operator or the installing engineer, not this code. What the guide carries is named, and what no guide can carry is said. |

A standard satisfied by a documented deployment step is **not** the same as one satisfied by code, and
the entries keep the two apart. Three cross-cutting notes apply to nearly every entry:

- **Product scope vs deployment practice.** This repository deliberately pushes several things to
  [`deploy/README.md`](../../deploy/README.md) and [`deploy/cloud/README.md`](../../deploy/cloud/README.md):
  certificates, passwords, backup, restore, rollback, and the broker's ACL. Those are real obligations
  with a real answer here, and the answer is prose an operator follows, not code that enforces it.
- **The security story is one day old.** The Gateway served plain HTTP until 2026-10-07
  ([ADR-0028](decisions/0028-the-gateway-serves-over-tls.md)). Anything in the entries below that reads
  as "the transport is protected" is true *from that date*, and the deployment must be upgraded for it
  to be true of a running plant.
- **Nothing here is verified by a third party**, and the tests named are the project's own.

---

## 1. The summary

**Twenty-nine** standards were assessed. **Two** are implemented, **eight** are partial, **three** are
not implemented, and sixteen are out of scope — thirteen because they are not this product's subject
(the protocols it does not speak and the sectors it is not in), and three — ISO/IEC 27001,
NIST SP 800-82 and GAMP 5 — because they bind an organisation or a deployment rather than a product.

***Rows 23–29 were added on 2026-10-08, and the reason they were missing is the finding.*** The first
pass asked *"which standards does this code meet"* and answered it well for everything the product
**speaks**. It did not ask *"which standards exist for the questions we decided ourselves"*, so seven
were absent — and **three of them cover decisions taken by taste on 2026-10-06 and 2026-10-07**: what an
equipment symbol looks like (ISA-5.1), what a colour on a plant display is allowed to mean (IEC 60073),
and what an alarm's model is (OPC UA Part 9). A fourth, ISA-112, is the only standard written for this
kind of product as a whole and was published while this project was being built. **An audit scoped to
what you already know you implement cannot find the thing you invented instead of looking it up.**

| # | Standard | Status | The one thing to know |
|---|---|---|---|
| 1 | **OPC UA / IEC 62541** (Parts 2, 3, 4, 8, 14) | **Partial** | Read path works; **Part 2 security is not implemented at all** — the client connects with `useSecurity: false` and an anonymous identity |
| 2 | **OPC DA** (quality model) | Out of scope as a protocol | Its `QQSSSSLL` quality structure is the ancestor of our four values; we carry neither substatus nor limit bits |
| 3 | **IEC 61850** (quality bits) | Out of scope | Substation protocol; its 13-bit quality is far finer than our four values, and mapping onto them would be lossy |
| 4 | **DNP3 / IEEE 1815** | Out of scope | No driver; its flags (remote forced, local forced, chatter) have nowhere to go in our model |
| 5 | **IEC 60870-5-101/104** | Out of scope | No driver; same reason, its quality descriptor has five bits we cannot hold |
| 6 | **IEC 62443** (4-1, 4-2, 3-2, 3-3) | **Partial** | The technical requirements have a real foundation; **the product-development lifecycle (4-1) is largely absent**. *(Its two named product gaps — no brute-force protection on login and no readable audit trail — were closed on 2026-10-07 by ADR-0031 and ADR-0032; see §3.1.)* |
| 7 | **ISO/IEC 27001** | Out of scope for the product | It certifies an organisation, not a piece of software; the supplier-side gaps are named under IEC 62443-4-1 |
| 8 | **NIST SP 800-82r3** | Deployment practice | Asset-owner guidance; the product supports some of it and the guide carries the rest |
| 9 | **IEEE 1686** | Out of scope | It is about IEDs (protection relays); this product configures none |
| 10 | **NERC CIP** | Out of scope | Jurisdictional: it binds a North American bulk-electric asset owner, not a product |
| 11 | **ISA-101** (HMI) | **Partial** | The look is disciplined and contrast-checked; there is no display hierarchy, no faceplate, and no mimic |
| 12 | **ISA-18.2 / IEC 62682** (alarms) | **Partial** | The engine has states, on-delay, deadband and a journal; **alarm priority does not exist**, so nothing can be rationalised |
| 13 | **ISA-95 / IEC 62264** | Out of scope | No MES/ERP interface, and our hierarchy is Tenant/Site/Device/Tag, which is not ISA-95's |
| 14 | **ISA-88** | Out of scope | No batch control, no recipes, no procedural model |
| 15 | **IEC 61131-3** | Out of scope | We are not a PLC programming environment; the Gateway scans and alarms, it does not run user logic |
| 16 | **21 CFR Part 11** | Out of scope by decision | No pharma claim; the append-only audit trail is already the hard half if that ever changes |
| 17 | **GAMP 5** | Out of scope | A validation framework for regulated manufacturing; it would follow Part 11, not precede it |
| 18 | **ISO 8601 and time handling** | **Implemented**, one gap | UTC everywhere, two timestamps per reading, clock skew journalled; **the Site's IANA time zone is stored and never used to display** |
| 19 | **IEC 61000-4-30** | Out of scope | It defines power-quality *instruments*; we display what a device said, and claim no measurement accuracy |
| 20 | **OPC UA FX / IEC/IEEE 60802** | Not implemented | Forward-looking field-level PubSub and TSN; no decision has been taken and none is needed yet |
| 21 | **IEC 61508** | Out of scope by decision | We are not a safety-related system, and no SIL may be claimed for us |
| 22 | **IEC 61511** | Out of scope by decision | Process-industry SIS standard; a SCADA is not the protection layer, and this one must never be used as one |
| 23 | **ANSI/ISA-112.00.01-2025** | **Not implemented** | *SCADA Systems — Part 1*, published February 2026: the only standard written for **this kind of product as a whole**, and nothing here is measured against it |
| 24 | **ISA-5.1 / ISO 14617** | **Partial** | The symbol conventions a plant engineer is trained on; ADR-0027's drawings were designed from first principles and never checked against them |
| 25 | **IEC 60073** | **Partial** | What a colour on an indicator is *allowed* to mean; our palette matches the convention and arrived there by reasoning, not by the standard |
| 26 | **OPC UA Part 9** (Alarms and Conditions) | **Partial** | The alarm model this industry already has: `Severity`, Acknowledge **and Confirm**, and shelving with a cap — we match the shelving rule and have no severity and no confirm |
| 27 | **Eclipse Sparkplug 3.0** | Out of scope **by decision** (ADR-0017) | The de-facto MQTT convention for SCADA; refused for our own payload, and **the refusal had never been assessed as a standard** |
| 28 | **MQTT 5.0** (OASIS) / ISO/IEC 20922 | **Implemented** | All three MQTT call sites pin `V500` explicitly; ISO/IEC 20922 is MQTT 3.1.1, so we are above the ISO publication rather than behind it |
| 29 | **EU Cyber Resilience Act** (2024/2847), with NIS2 | **Not implemented** | **Law, not guidance**, for a product with digital elements sold in the EU: vulnerability reporting from 11 September 2026, the rest from 11 December 2027 |

---

## 1.5 ISA-112 — **the only standard written for this kind of product, and it is newer than this project**

**What it is.** `ANSI/ISA-112.00.01-2025`, *SCADA Systems — Part 1: SCADA Lifecycle, Diagrams and
Terminology*, announced by ISA in February 2026
([ISA's announcement](https://www.processingmagazine.com/news-notes/news/55359937/new-isa-112-part-1-standard-targets-scada-modernization-and-system-governance),
[Control Engineering's tutorial](https://www.controleng.com/tutorial-inside-look-at-the-isa112-scada-lifecycle-standard/)).
The committee was formed in 2016 and now numbers over 350 members. **I did not read it** — it is sold by
ISA — so what follows is from those two secondary descriptions and is deliberately thin.

Its central idea, as both sources state it, is that **a SCADA system is a long-lived operational asset
rather than a one-time capital project**: standard lifecycle phases, continuous work processes,
standardised terminology and system diagrams. Further parts on lifecycle review processes and on SCADA
architectures are planned and unpublished.

**Why it belongs at the top of this document.** Every other standard here is about one *aspect* — a
protocol, a security property, a display convention. This is the one whose subject is the thing being
built. It is listed first for that reason, not because it is the most urgent.

**Status: not implemented, and that is a statement about vocabulary, not about quality.** Nothing in this
repository is expressed in ISA-112's terms. The phase plan is this project's own lifecycle, invented
here; `open-work.md` is its own register; the terminology — Site, Device, Tag, Edge, Screen — was defined
by ADR-0001 and ADR-0004 without reference to it. **None of that is necessarily wrong**, and the
architecture is not in question; what is missing is any check of whether our words mean what the
industry's words mean. Where they differ silently, an integrator reading our documentation will
mis-translate it into their own.

**What it would cost, and the honest recommendation.** Buying Part 1 and reading it against
`phase-plan.md`, ADR-0001 and ADR-0004 is **a day**, and its output is a terminology mapping rather than
code. **Worth doing before the first customer-facing document**, and not worth interrupting a phase for
— with one exception: if the terminology diverges, fixing names costs more every week. Part 1 was
published in February 2026; the bulk of this project's vocabulary was fixed before that, so this is a
case of the standard arriving late rather than of the project ignoring it.

**It is not a substitute for the gaps in §9.** A lifecycle standard does not implement OPC UA security
or alarm priority. It is a frame, and a frame on a product with three open gaps is still a product with
three open gaps.

---

## 2. The protocols this product speaks

### 2.1 OPC UA and IEC 62541 — **Partial**

**What it is.** OPC UA is the OPC Foundation's service-oriented industrial protocol, published as
IEC 62541 in parts: Part 2 is the security model (a Technical Report, `IEC/TR 62541-2`), Part 3 the
address space model, Part 4 the services, Part 8 Data Access, Part 14 PubSub, Part 9 Alarms and
Conditions ([Wikipedia's part table](https://en.wikipedia.org/wiki/OPC_Unified_Architecture)). It is in
scope because this product ships an OPC UA *client* driver
([ADR-0006](decisions/0006-technology-stack.md)), and it is the one protocol here with a security model
worth meeting.

**Implemented.** `Drivers.OpcUa` reads nodes named by an `endpointUrl` plus a per-tag node address:
`OpcUaAddress.TryParse` rejects a misconfigured address as a permanent fault rather than a read failure,
and a read that cannot be performed comes back as a reading with a quality, not as a missing tag — which
is Part 4 and Part 8's read path used honestly, and the shape ADR-0003 requires. Values keep the
server's source timestamps. The driver builds a full certificate configuration — application
certificate, trusted peer list, trusted issuers, rejected store — and checks its own instance
certificate at connect (`src/Modules/Drivers.OpcUa/OpcUaDriver.cs`, `BuildConfigurationAsync`), and a
stand-in driver for tests never reports Good unconditionally because the real one does not.

**Not implemented, and named.**

- **Part 2, the security model, in full.** `ConnectAsync` selects the endpoint with `useSecurity: false`
  and creates the session with `new UserIdentity(new AnonymousIdentityToken())`
  (`OpcUaDriver.cs:80-101`), with a comment that says the choice was deliberately deferred "to the auth
  work in Phase 5". Phase 5 shipped on 2026-09-11 and did not pick it up; ~~**no ADR anywhere records
  this decision**~~ — *corrected 2026-10-08: it is recorded now, and as a reversal rather than a
  deferral.* [**ADR-0033**](decisions/0033-the-opc-ua-driver-negotiates-security.md) decides that the
  driver negotiates the strongest endpoint a server offers, refuses one that offers none, and makes an
  unsecured session a per-device opt-out that is logged. **This row stays `Partial` until that is
  built** — the decision is in force, the code is not written, and those are two different claims. The
  consequence below is what the code still does today, and is concrete:
  every value this product reads from an OPC UA server crosses the plant network **unencrypted and
  unauthenticated**, and a device that answers on that address is trusted by default as far as the
  session is concerned. `acceptUntrustedCertificates` defaults to false
  (`OpcUaDriverFactory.cs:38-43`) — which is the right default and, with security off, currently decides
  nothing.
  **Cost:** a slice of its own — a per-device security policy and certificate settings, a trust list the
  deployment owns, and the refusal path when a server's certificate is not trusted. **It needs an ADR
  first**, because it decides what a deployment must trust and what happens when it does not. **Rough
  size:** a week of implementation plus the deployment guide's certificate section, and it inherits the
  renewal story already written for the edge certificates.
- **Part 8's finer value model**: per the normative Annex A.4.3.3, an OPC UA status code carries a
  severity, a subcode and **limit bits**, and Part 8 §7.3 defines the limit bits a Data Access client is
  expected to understand. Our [`Quality`](../../src/Core/Model/Quality.cs) holds four values
  (Good/Uncertain/Bad/Stale) and [`TagValue`](../../src/Core/Model/TagValue.cs) holds a value and no
  limit state, so "the value is at or beyond a limit the device reports" cannot be represented. **Cost:**
  small in code, **not small in consequence** — it changes ADR-0003, so it needs a decision, and the
  honest default is that a deployment which needs limit bits has to say so.
- **Subscriptions, browsing and Part 9 (Alarms and Conditions).** The driver polls with
  `ReadAsync`; a device's own alarms and conditions are not read, so an OPC UA server's alarm tree never
  reaches this product. That is a scope decision rather than a defect — the alarm engine evaluates
  thresholds itself ([ADR-0025](decisions/0025-an-alarm-waits-before-it-announces-itself.md)) — but it
  means a plant that rationalised its alarms in the device will not see them here, and **nothing records
  that refusal**. Worth an entry in `open-work.md` §3 rather than a code change.
- **Part 14 PubSub.** Not implemented, and deliberately so: [ADR-0017](decisions/0017-edge-to-cloud-link.md) §2
  chose *our own payload over MQTT rather than Sparkplug B*, and PubSub is a different transport for the
  same job. The refusal is recorded and reasoned; what is **not** correct is that three documents still
  name Sparkplug B as something this product speaks. **Checked while this document was reviewed rather
  than taken from it, and the first count here was wrong in the other direction — it said five mentions
  in three documents, and one of them (`phase-plan.md` line 711, *"the payload is ours rather than
  Sparkplug B"*) says the opposite and is correct.** Verified line by line on 2026-10-07, there are
  **four places, and all four are now corrected**: `phase-0-architecture.md` lines 25 and 62 (the driver
  list and the edge link), `phase-plan.md` line 269 (*"MQTT/Sparkplug B is push"*, which named a
  specification this project rejected as the illustration for MQTT being push), and — the one that
  matters most — **ADR-0006's own driver-layer table**, which read "MQTTnet (MQTT / Sparkplug B)" for as
  long as the table has existed. An ADR is binding, so a table inside one claiming a payload ADR-0017 §2
  rejected is the stale sentence the ADR README's amendment rule exists for: no decision changed, the
  sentence stopped being true. `CLAUDE.md` and ADR-0017 said it correctly throughout, which is what made
  the others look deliberate rather than forgotten.

**Also worth saying plainly:** there is no OPC UA *server* here. A third-party system cannot read this
product's tags over OPC UA; the only interfaces out are the REST API and the SignalR hub.

### 2.2 OPC DA's quality model — **out of scope as a protocol, and a gap in what we inherited**

**What it is.** The original OPC Data Access specification, whose quality structure is `QQSSSSLL`: a
two-bit primary quality (Good/Uncertain/Bad), a four-bit substatus and two limit bits. It is still the
mapping target of OPC UA Part 8's normative annex, which maps UA status codes onto those primary
qualities ([OPC 10000-8, A.4.3.3](https://reference.opcfoundation.org/specs/OPC-10000-8/a-4-3-3)).

**Where we stand.** This product does not speak OPC DA and has no plans to, so the protocol is out of
scope. What matters is that **we inherited its idea and kept a quarter of it**: four qualities, no
substatus, no limit bits. `Quality.cs` says so itself — "modelled on OPC UA's quality-code concept
(ADR-0003)" — and four values is what every driver maps onto. **The cost of the missing substatus is
real but small today** (a maintenance engineer cannot tell "sensor failure" from "communication failure"
in the UI, because both are `Bad`); the cost of the missing **limit bits** is the one above. **Decision
needed, not code.**

### 2.3 IEC 61850 — **out of scope**

**What it is.** The substation automation standard; its `Quality` (IEC 61850-7-3) is a 13-bit string —
two bits of validity (Good/Invalid/Reserved/Questionable) plus overflow, outOfRange, badReference,
oscillatory, failure, oldData, inconsistent, inaccurate, source (process/substituted), test and
operatorBlocked ([a library implementing it, read as the source for the bit list](https://docs.rs/iec61850-rs/latest/src/iec61850_rs/common/quality.rs.html)).
Out of scope: no IEC 61850 driver exists, and the standard is about substation protection and
automation, not about supervising a plant through a browser.

**Why it is still in this document.** It is the clearest statement of how much fidelity a mature
industrial protocol puts into quality, and it is the strongest argument that our four values are a
*display* decision rather than a protocol-faithful one. **If an IEC 61850 client is ever added, mapping
those thirteen bits onto four values loses information in a way an operator cannot see**, exactly the
class of loss ADR-0003 exists to prevent. The order of work is therefore: decide the quality model
first, then write the driver.

### 2.4 DNP3 / IEEE 1815 — **out of scope**

**What it is.** The Distributed Network Protocol, an IEEE standard for SCADA telemetry, mainly in
electric utilities and water. Each point carries a quality byte whose bits name Online, Restart,
Communications lost, Remote forced, Local forced, Chatter filter, Over range and Reference error
([a DNP3 implementation's own table, read as the source](https://help.plc.abb.com/dnp3_quality_flag.html)).
Out of scope: there is no DNP3 driver, and adding one is a driver slice on the existing framework
(ADR-0002), not an architectural change.

**The finding.** Look at what those flags mean: **Remote forced** and **Local forced** say that a person
or a program has overridden the field value. This product's model has nowhere to put that, so a DNP3
device could be lying to us with our own quality field saying `Good`. Any future DNP3 work inherits the
quality decision from §2.2 first.

### 2.5 IEC 60870-5-101/104 — **out of scope**

**What it is.** The telecontrol companion standard widely used in European utilities; 104 is the
TCP/IP profile. Its quality descriptor is a small set of bits — invalid, not topical, substituted,
blocked, overflow — as an implementation of it names them
([go-iec104, read as the source for the bit names](https://pkg.go.dev/github.com/yobol/go-iec104)).
Out of scope: no driver, same reasoning as DNP3, and the same quality-model prerequisite. Note that
"**substituted**" and "**blocked**" are again states this product cannot represent.

### 2.6 Modbus — not a security standard, and the reason to say so

Not in the owner's list, and it belongs here anyway because it is this project's Phase 1 driver and the
protocol most plants actually use. Modbus TCP has **no authentication, no integrity protection and no
quality code at all**; a register read is a number or a timeout. That is why ADR-0003 is written the way
it is, and why the drivers name the reason a tag has no value rather than inventing one
(`ModbusTcpDriver`, and ADR-0003's silence rule as applied in ADR-0016 and ADR-0023). It is also the
reason **network segmentation is not optional for a Modbus plant** — see IEC 62443 §3.6 below. The
protocol itself offers nothing to conform to, so it carries no status row.

### 2.7 Eclipse Sparkplug 3.0 — **out of scope by decision, and the decision had never been assessed**

**What it is.** The de-facto convention for MQTT in industrial SCADA: a prescribed topic namespace and a
state-management model, published as *Sparkplug Topic Namespace and State Management* and, at **3.0**,
the first version managed under the Eclipse Foundation specification process — which formalised 2.2,
resolving ambiguities and adding normative statements rather than adding features
([the specification's version page](https://sparkplug.eclipse.org/specification/version)). Eclipse Tahu
is the reference implementation. **I did not read the specification**; what follows is from that page
and from widely-agreed secondary descriptions, which is why the mechanics below are named rather than
quoted.

Its substance, as those sources describe it: **birth and death certificates** — `NBIRTH`/`DBIRTH`
publishing a node's or device's full metadata and current values on connect, `NDEATH`/`DDEATH` on loss,
with `NDEATH` registered as the MQTT Last Will and Testament — and a retained `STATE` message by which a
host application declares itself online, from which an edge decides whether to publish and when to
rebirth.

**ADR-0017 chose our own payload over it, explicitly.** That decision stands and this entry does not
reopen it. What this entry records is that **until 2026-10-08 it had never been assessed as a standard**:
Sparkplug appears seven times in this document and in `phase-plan.md` and ADR-0006, and *every one of
those mentions was about documentation accuracy* — correcting places that wrongly implied we speak it
(§2.1). The thing itself was never weighed.

**Weighed now, the decision still looks right, and for a reason worth writing down.** Sparkplug's model
assumes the edge is the authority on its own device list: a node announces what it has, and the host
learns it. **ADR-0019 decides the opposite** — the cloud is the source of truth and *derives* each edge's
configuration onto the link, and ADR-0022 goes further by making the link device underivable from
anything an operator types. Those are not two ways of spelling the same thing; they are opposite answers
to *who decides what an edge reads*. Adopting Sparkplug would have meant adopting its answer.

**What we do match, having arrived there separately**, which is the interesting part: a declaration
published on connect carrying the edge's drivers and devices (ADR-0021, and its version 2 bump), a
retained configuration topic, Last Will on the link, and a buffer that records the window it lost
(ADR-0017). The shapes converged because the problem is the same shape.

**What it costs to have refused it.** An integrator with a Sparkplug-speaking historian or broker-side
tool cannot point it at our link; they must read our payload. That is a real cost and it is the price of
ADR-0019's model, not an oversight. **Status stays out of scope by decision** — now a decision that has
been examined rather than one that was merely made.

### 2.8 MQTT 5.0 / ISO/IEC 20922 — **Implemented**

**What it is.** MQTT 5.0 is the OASIS standard; **ISO/IEC 20922 is the ISO publication of MQTT 3.1.1**,
the older version. Being "above ISO/IEC 20922" is therefore the right place to be, and saying "we
conform to ISO/IEC 20922" would be the wrong claim rather than a stronger one.

**Implemented, and consistently — checked rather than assumed.** All three places this product speaks
MQTT pin version 5 explicitly: `UplinkService.cs:94`, `EdgeConfigurationPublisher.cs:118` and
`MqttPushingDriver.cs:80`, each `.WithProtocolVersion(MqttProtocolVersion.V500)`. There is no code path
that negotiates down to 3.1.1, and nothing relies on a default.

**This matters more than a version number usually does.** MQTT 5 is what makes ADR-0023's write path
possible to express honestly — correlation data and response topics for matching a reply to its call —
and it carries reason codes, so a refusal can say *why* rather than just dropping the connection, which
is ADR-0003's rule arriving in the transport. A deployment pinned to a 3.1.1-only broker would lose
those, and nothing currently checks the broker's version at connect. **The one gap here**: a broker that
only speaks 3.1.1 is a connection failure rather than a message naming the cause.

---

## 3. Security

### 3.1 IEC 62443 — **Partial**

**What it is.** The IEC/ISA 62443 series is the industrial cybersecurity standard family, in four
relevant pieces: **4-1** is the *secure product development lifecycle* a supplier must follow;
**4-2** is the *technical security requirements for IACS components* (the product requirements, in
twelve subject areas, plus four "common component security constraints" — CCSC 1 to 4); **3-2** is
security risk assessment and system design (zones and conduits, and the ZCR 1–7 process that derives a
target security level per zone); **3-3** is system security requirements and security levels (SL 0–4).
Sources read: [the series and its parts, with the 4-1 practices, the 4-2 subject areas and CCSCs, the
3-2 ZCR process and the security levels](https://en.wikipedia.org/wiki/IEC_62443).

It is in scope, and it is the standard this product should be measured against first: the owner is the
*product supplier* (4-1/4-2 apply to what we build), and the plant's operator is the *asset owner*
(3-2/3-3 apply to the installation we are part of). **The 4-2 requirement numbers are not quoted below
because I did not read 62443-4-2**; the requirements are named instead, and the numbering is flagged as
unverified.

**Implemented, with the evidence.**

| What 62443 asks of a component | How it is met here |
|---|---|
| Unique identity per human user | ADR-0011's per-user accounts, opaque session tokens stored hashed — `src/Gateway/Security/SessionStorage.cs`, `tests/Persistence.Tests/SessionStorageTests.cs`, `tests/Core.Tests/SessionManagerTests.cs` |
| Account management, deactivation, role change taking effect | ADR-0011; deactivation blocks the *next* request, and `tests/Gateway.Tests/LiveAccessTests.cs` proves revocation reaches an **already-open** SignalR connection |
| Strength of password-based authentication | ADR-0011: 12-character minimum, no composition rules, `PasswordHasher` (PBKDF2-HMAC-SHA512 at 100k at the time of writing); hashes never reversible — `tests/Gateway.Tests/CredentialStorageTests.cs` |
| Session integrity: expiry, logout, revocation | ADR-0011's idle (12 h) and absolute (7 day) limits, swept; logout ends the session server-side |
| Authorisation, least privilege, deny by default | ADR-0011's Viewer/Operator/Admin split, Site-scoped, enforced server-side; a Site the caller cannot see answers **404 rather than 403** so paths cannot be used to enumerate — `tests/Gateway.Tests/SiteScopingTests.cs`, `RolePermissionTests.cs` |
| Least privilege *in the data layer* | The application connects as a non-superuser role with `INSERT`/`SELECT` only on the audit and alarm-journal tables — `src/Persistence.TimescaleDb/ApplicationRole.cs`, `tests/Persistence.Tests/ApplicationRoleTests.cs`, and a startup check that **refuses a connection which could rewrite the audit log** (`tests/Gateway.Tests/StartupCheckTests.cs`) |
| Non-repudiation of operational acts | Append-only `audit_log` rising from ADR-0011, enforced at the database (`tests/Persistence.Tests/AuditLogAppendOnlyTests.cs` — which deliberately runs over the application's own connection, because a superuser proves nothing) |
| Audit of security-relevant events | Authentication events and permission-relevant writes go through `IAuditLog`; endpoint wrapping in `src/Gateway/Security/AuditedEndpoints.cs` |
| Communication integrity and confidentiality | ADR-0028 (web surface, TLS terminated by the Gateway or declared upstream, refused otherwise) and ADR-0017 §3 (the edge link: TLS, one client certificate per edge, QoS 1 over MQTT 5, buffer released only on acknowledgement) |
| PKI / certificate-based identity for components | Per-edge certificates whose **name is the identity**, confined by `deploy/cloud/mosquitto/acl`; the deployment guide carries generation, lifetimes (825 days per certificate, 10-year CA) and the renewal consequence (`deploy/cloud/README.md`) |
| Timestamps that can be trusted and are attributed | UTC throughout, a source timestamp and an ingestion timestamp per reading (ADR-0003), and a **clock-skew tolerance with a journalled entry** rather than a silently corrected clock — `src/Gateway/Scanning/PushedSourceSettings.cs` (30 s default), `src/Core/Tags/PushedSourceRecorder.cs` |
| Backup and recovery | `deploy/README.md` documents backup, restore **and rollback**, tested end to end in Phase 6 rather than described |
| Restricted data flow between components | Separate Compose networks, a broker ACL that lets each edge read only its own configuration and answer only under its own name, and a Gateway that may read every edge's answer (`deploy/cloud/mosquitto/acl`; `tests/Gateway.Tests/BrokerConfigurationTests.cs`) |

**Not implemented, named, with cost.** In rough order of what a reviewer would raise first:

1. **No brute-force protection — closed on 2026-10-07 by
   [ADR-0031](decisions/0031-the-sign-in-path-is-hardened.md), and the estimate here held.** Five
   consecutive failures lock the account for fifteen minutes; the count lives on the user row, so a restart
   does not clear it; **an expired lock starts a fresh count**, because a person locked out last week who
   mistypes once today has not earned a second one; a success clears it and an Admin's password reset clears
   it. The locked account **is told** it is locked, deliberately, and the ADR argues the trade rather than
   hiding it. **One thing this document called cheap turned out to be a decision**: per-caller limiting is
   *not* built, because behind the proxy ADR-0028 allows, `RemoteIpAddress` is the proxy — and a forwarded
   header trusted without deciding which hops may set it is a bypassable control that looks present. The ADR
   is on `main` and the code is in PR #10.
2. **The audit trail cannot be read by the product — closed on 2026-10-07 by
   [ADR-0032](decisions/0032-the-audit-trail-can-be-read.md).** An Admin reads it in the product now, newest
   first, filtered by action prefix, actor, subject and window, with **the matching total beside every
   page** so a cap cannot pass for a whole trail. Two of this document's own claims were wrong and the
   correction is worth keeping: **"No ADR needed" was wrong** — *who* may read the trail is exactly the
   question the caveat above says the role table does not answer, and the answer is Admin, because an audit
   row carries no Site and there is no honest Site filter over it; and **the read is not by time**, because
   `occurred_at` is not unique — ADR-0031's lock writes two rows in the same millisecond — so pages are cut
   by `id` and a boundary cannot repeat or drop a row. The ADR is on `main` and the code is in PR #10.
3. **No vulnerability handling and no disclosure path.** There is no `SECURITY.md`, no
   `.github/` directory, no contributing guide, no CI of any kind (`open-work.md` §3 records "CI — any
   pipeline at all" as needing a phase that wants it), and therefore no build-time scanning, no signed
   releases, no patch or release process. What exists is *manual and measured once*: `npm audit` (nine
   advisories, all in the Angular build chain, none of which ship) and
   `dotnet list package --vulnerable --include-transitive` (none), both recorded in `open-work.md` §4.
   62443-4-1 asks for a lifecycle: security requirements, threat modelling, secure coding, security
   testing, vulnerability management, update publication. **Cost: the largest item in this document** —
   a CI pipeline, a dependency-scanning step, a release/disclosure policy, a threat model per release,
   and a security-requirements register. **It needs a decision** (it is a change to how this project is
   developed, and "who reviews a security report" is the owner's question, not the code's).
4. **No software component inventory (SBOM).** Nothing generates one. 62443-4-1 and 62443-4-2 (through
   CCSC 4, which requires a 4-1-conformant development process) both lean on knowing what is in the
   product. **Cost: small** — an SBOM step in the build, and a decision about who receives it.
5. **No security event monitoring.** Nothing watches the audit trail, nothing alerts on repeated failed
   logins, nothing exports security events to anything. The broker has its own refused-publish audit log
   (`deploy/cloud/README.md`), and that is the whole of it. **Cost: medium**, and it depends on (2).
6. **No retention or rotation for the audit trail or the alarm journal.** ADR-0013 says retention for
   the journal "is left untouched until a real deployment gives a reason to set one", and the same is
   true of `audit_log` and of the historian itself: nothing purges, compacts or ages anything. Growth
   is unbounded and no policy says what must be kept. **Needs a decision** (retention is a compliance
   question before it is a storage question).
7. **No HTTP security headers, and the session token lives in `localStorage`.** No
   `Content-Security-Policy`, no `X-Frame-Options`, no `X-Content-Type-Options` anywhere (grep: the only
   header-related code is `UseHsts`, and only when the deployment turns it on per ADR-0028 §5).
   `src/Web/src/app/auth.ts` keeps the session token in `localStorage` under `scada-darbox.session`, so
   any script the page executes can read a working Operator credential. HSTS being off by default is a
   *recorded* decision with a good reason; the missing headers are not recorded anywhere. **Cost: small
   to medium** — a CSP is easy to add and needs care with the dev server; clickjacking protection is
   one header. **No ADR needed**, but the CSP policy is worth writing down where the client's origin
   rules already live.
8. **The audit trail and the database are not encrypted at rest, and backups are not encrypted.** The
   guide's backup is a plain `pg_dump` file copied to another disk. Nothing in this repository discusses
   encryption at rest, and the honest position is that it is the deployment's responsibility (filesystem
   or volume encryption) — **which no guide currently says**. **Cost: a paragraph in the guide, plus a
   decision about whether the product encrypts its own backups.**
9. **Zones and conduits (3-2) are not the product's to draw**, but nothing in the deployment guide even
   *names* the concept: it documents networks and certificates without saying that the customer's
   security team will ask for a zone-and-conduit diagram and a target security level per zone. The
   Gateway refuses to serve without TLS (ADR-0028 §3), which is the one strong default here. **Cost: a
   section in `deploy/README.md`** — materials an integrator can lift into their own 3-2 work.

**Deliberately out of scope, and why.** 62443-2-1 (the asset owner's security programme) and 62443-2-4
(the service provider's capabilities) bind the plant and the integrator, not this code; the product's
duty there is to be *documentable*, which is what item 9 above asks for. **No certification is claimed
or planned**: SL 1 to SL 4 are per-requirement targets derived from an asset owner's risk assessment,
and a product cannot declare itself at a level. The honest statement is the one at the top of this
document: nothing here has been assessed by anyone.

### 3.2 ISO/IEC 27001 — **out of scope for the product; the supplier-side gaps are named above**

**What it is.** The international standard for an information security management *system*: an
organisation defines a scope, manages risk, and selects controls (the 2022 revision restructured
Annex A; the change is described by
[BSI's summary of the 2022 update](https://hcms.bsigroup.com/en-PH/isoiec-27001-information-security-management/isoiec-27001-revision/)).
It certifies an organisation, so **it is not something this repository can implement** — a product is
not 27001-certified; the company that makes it can be. It is in this document because a serious SCADA
supplier is usually asked for it, and because ISO 62443-2-1 explicitly points an asset owner's programme
at it.

**What exists here that contributes.** The ADR discipline (a decision is written, dated, superseded
rather than edited, and reviewed against), the test discipline (mutation-verified guarantees), the
`open-work.md` register, the deployment guide's backup/restore, and the measured dependency checks in
`open-work.md` §4. **What does not exist**: a risk register, a statement of applicability, an incident
response plan, supplier and personnel security, business continuity, internal audit, or any management
review. Those are organisational, **not code**, and the entry is here so that nobody mistakes "the
software is careful" for "the organisation is certified". **Nothing to implement in this repository.**

### 3.3 NIST SP 800-82r3 — **deployment practice, with real product overlap**

**What it is.** NIST's *Guide to Operational Technology (OT) Security*, Revision 3, published September
2023, superseding Rev 2; it is guidance rather than a certifiable standard, and its keywords are
explicitly DCS, ICS, PLC and SCADA ([NIST CSRC publication page](https://csrc.nist.gov/pubs/sp/800/82/r3/final)).
A Revision 4 initial public draft is open for comment ([same page](https://csrc.nist.gov/pubs/sp/800/82/r3/final)).

**Where we stand.** It is written for asset owners and integrators, so most of it is deployment
practice. The parts a *product* can support, and which this product does support, are already listed
under IEC 62443 above: identity and authorisation, audit, TLS, least privilege in the data layer,
network separation, backups. The parts it recommends and this product does **not** have are the same
list: account lockout, security event monitoring and alerting, retention policy, component inventory.
One 800-82 theme deserves a specific mention in the other direction — **the guide is emphatic that OT
availability and safety outrank confidentiality**, and several decisions here already follow that
ordering (HSTS off by default because a lockout during an incident is worse than a downgrade,
ADR-0028 §5; the alarm engine's bounded shelving; the edge buffer's bounded-drop-with-a-record). Those
are worth citing when an IT security reviewer asks why this product does not behave like a web app.

### 3.4 IEEE 1686 — **out of scope**

**What it is.** *IEEE Standard for Intelligent Electronic Devices Cybersecurity Capabilities* — current
edition IEEE 1686-2022, with a 2025 corrigendum; it defines the functions an IED must provide for
access, operation, configuration, firmware revision and data retrieval, and addresses the
confidentiality, integrity and availability of its external interfaces
([IEEE SA standard page](https://standards.ieee.org/ieee/1686/7207/)). Out of scope: this product is not
an IED and does not configure or manage one. It would enter scope only if a driver ever managed
protection relays, and then it would be the relay's certification, not ours.

### 3.5 NERC CIP — **out of scope (jurisdictional)**

**What it is.** The North American Electric Reliability Corporation's Critical Infrastructure Protection
standards, binding on registered entities of the bulk electric system; the set runs from CIP-002
(categorisation) through physical security (CIP-014, currently at −4 in a 2026 filing, with CIP-015-2
proposed) — see NERC's own filing material
([CIP-014-4 petition](https://www.nerc.com/globalassets/who-we-are/legal--regulatory/filings--orders/nerc-filings-to-ferc/2026/petition-for-approval-of-cip-014-4.pdf_final_digicert.pdf)).
Out of scope: it binds an asset owner in that jurisdiction, and whether this product may be used there is
a question for that owner's compliance team. **I did not verify the sub-requirement numbering of any CIP
standard**, and this document deliberately does not use it. What can be said honestly: a SCADA product
used in that sector is usually asked to help with the *systems security management* obligations
(accounts, ports and services, security event monitoring, patch management), and the gaps named under
IEC 62443 — no lockout, no monitoring, no patch process, no inventory — are exactly the ones such a
review would raise.

### 3.6 EU Cyber Resilience Act, with NIS2 — **Not implemented, and it is law rather than guidance**

**What it is.** Regulation (EU) 2024/2847, in force since **10 December 2024**, covering *products with
digital elements* placed on the EU market. Two dates matter
([Freshfields' timeline](https://freshfields.us/insights/campaigns/technology-quotient/tech-and-platform-regulation/eu-digital-strategy/cyber-resilience-act),
[DLA Piper's summary](https://www.technologyslegaledge.com/2024/11/navigating-the-european-cyber-resilience-act-key-dates-and-obligations/)):

- **11 September 2026** — manufacturers must report an **actively exploited vulnerability to ENISA within
  24 hours**, with a full report in 72 hours, and the same for severe incidents.
- **11 December 2027** — the rest, including conformity assessment and CE marking.

Manufacturers must also run a vulnerability-handling process **for the expected product lifetime or five
years, whichever is shorter**. Sector-regulated products (medical devices, aviation, vehicles) are carved
out; SCADA is not. **I did not read the regulation**; the dates above are from two law-firm summaries and
should be confirmed against the text on EUR-Lex before anything is promised to a customer. No source I
found addresses how the CRA classifies SCADA software specifically — whether it falls in a higher-risk
annex is an open question with a real cost attached, and it is a question for a lawyer, not for this
document.

**Why it is here and not under ISO/IEC 27001's "binds an organisation" exemption.** The CRA binds the
**product and its manufacturer**. It is the one item in this document that is neither a technical
standard nor optional: *this is a legal obligation if this product is ever sold into the EU*, and North
Macedonia's market is oriented there.

**Status: not implemented, and it is the same gap as §9's first one wearing a deadline.** The CRA's
technical requirements are largely IEC 62443-4-1's — secure development, an SBOM, a vulnerability
intake and disclosure path, security updates for a declared support period, a declared end of support.
This repository has **none of them**, which §9 gap 1 already says. What this entry adds is that the gap
now has **a date against it** and the cheapest piece is the one with the earliest date: the
24-hour reporting duty of September 2026 needs a **published contact and a documented process**, not a
pipeline, and could be done in an afternoon. The expensive half — conformity assessment — has a year
more.

**NIS2 (Directive (EU) 2022/2555) is the other side and does not bind us.** It binds *operators* of
essential and important entities — which is what a water utility or an energy company running this
product is. It reaches this product indirectly and usefully: a customer under NIS2 will ask their
suppliers for exactly what the CRA requires, so the two push the same way.

**This is an owner decision, not an engineering one.** Whether this product is placed on the EU market,
who is named as manufacturer, who receives a vulnerability report and who signs a release are questions
only the owner can answer, and §9 gap 1 already says the same of the lifecycle. Recorded here so that
the date is on the record rather than discovered.

---

## 4. What an operator sees

### 4.1 ISA-101 — **Partial**

**What it is.** `ANSI/ISA-101.01-2015`, *Human-Machine Interfaces for Process Automation Systems*
([ANSI's announcement of the standard](https://blog.ansi.org/ansi/ansi-isa-101-01-2015-hmi-for-process-automation/),
[the standard's listing](https://shop.standards.ie/en-ie/standards/ansi-isa-101-01-2015-575556_saig_isa_isa_1317095/)).
It is the process industry's HMI standard: an HMI *philosophy*, a *style guide* and a *toolkit*, managed
through a lifecycle with periodic audit, and the practice of display hierarchies that most high-performance
HMI work follows (the level-1 overview / level-2 unit / level-3 detail convention, which is practice
described in industry writing such as [Modern DCS Graphics](https://5382318.hs-sites.com/modern-dcs-graphics-level-1-overview-displays)
rather than something I read in the standard). **I did not read ISA-101.01**: it is paywalled, and the
detail below is judged against secondary descriptions
([InstMC's HMI presentation](https://www.instmc.org/_userfiles/pages/files/technical/human_machine_interfaces_for_process_automation_systems_presentation.pdf)).

It is in scope because this product *is* the HMI: ADR-0024 makes a screen configuration rather than code,
and a screen here is what an operator looks at.

**Implemented, and better than most in one respect.**

- **Colour is a rule, not a decoration.** `src/styles.css` holds every colour, radius, shadow and type
  size as a token; **no other file in `src/app` writes a raw colour**, and the only strong colours are
  the semantic status ones — Good, Uncertain/Stale, Bad, no reading. The accent is deliberately
  hue-less. That is ISA-101's "colour carries meaning" position enforced by structure rather than by
  style guidance.
- **Quality is always visible.** ADR-0024 §4: there is no component kind that prints a value with
  nowhere for its quality to go, and a binding the reader may not see renders as unreadable rather than
  disappearing.
- **Contrast is measured, not asserted.** `src/Web/tests/theme-contrast.test.mjs` checks both the light
  and dark palettes against the AA thresholds for three text levels and every status pill; three real
  failures were found and fixed, one of them by deleting a near-duplicate token
  (`open-work.md` §2.0j).
- **Animation is bounded.** ADR-0027: a symbol may animate, **a continuing value never drives an
  animation**, and quality overrides the state — a Bad tag never draws a turning pump, because a
  turning pump is a claim about the world.
- **Sizes were judged on a panel.** The 1920×1080 review found three defects that were each right at
  1440 and wrong at 1920, including a fixed-size reading replaced by one that scales with the viewport,
  and a tile that printed the full display path replaced by one that prints the tag's name with the path
  in a tooltip (`open-work.md` §2.0j; `src/Web/tests/tile-layout.test.mjs`).

**Not implemented, named.**

- **No display hierarchy and no navigation model.** There is a list of screens per Site and a browse
  tree for configuration; there is no level-1/level-2/level-3 structure, no call-up from an alarm to the
  display that explains it, and no "what is this tag bound to" faceplate popup from a reading. **Cost:
  a slice**, and it needs a decision — ADR-0024 scoped components and deliberately kept nesting and
  free positioning out ("if a real deployment needs them"), and a navigation model is that kind of
  decision.
- **No mimics.** A screen is a grid of components; there is no free positioning, no piping, no
  equipment-to-equipment drawing. Recorded as a limitation, not an oversight: `open-work.md` §2.0j and
  ADR-0024 both say so, and the pump symbol is the smallest step in that direction.
- **Trend interaction is minimal.** The chart states its period, plots against the window asked for,
  reduces honestly, and breaks at holes (ADR-0029 and its walk) — but there is **no cursor, no tooltip,
  no zoom, and no read-the-value-at-a-time interaction**, which is what an operator actually does with a
  trend when a value is in question. **Cost: a client slice**; no ADR needed.
- **No alarm banner hierarchy or display-level alarm indication.** The alarms component lists standing
  alarms, and the summary table on the browse view is a table; there is no persistent banner with
  unacknowledged-first ordering, no per-display alarm indication, and no prioritisation — which is the
  ISA-18.2 gap in the next entry, seen from the HMI side.
- **No help, no units helper, no operator-level preferences.** Units are modelled properly (ADR-0005)
  and displayed, but there is no context help, and the theme choice is a browser preference rather than
  anything a deployment can set centrally.
- **The philosophy and style guide themselves are deployment documents.** ISA-101 expects the *site* to
  have them; this repository has the tokens and the rules that a guide would otherwise restate by hand.
  That is an unusual and good position to be in, and it is worth saying to a customer: the toolkit ships,
  the philosophy is theirs to write.

### 4.2 ISA-18.2 / IEC 62682 — **Partial**

**What it is.** The alarm management standard for the process industries, published by ISA as
ANSI/ISA-18.2 and by IEC as 62682 (the earlier guide being EEMUA 191); it defines the alarm lifecycle,
the alarm states, and the practices of rationalisation, prioritisation and performance monitoring
([Alarm management, with the standard's names and the related guides](https://en.wikipedia.org/wiki/Alarm_management)).
The state names commonly used come from the 2009 edition — Normal, Unacknowledged, Acknowledged, Return
to Normal Unacknowledged, Latched Unacknowledged, Latched Acknowledged, Shelved, Suppressed by Design,
Out of Service ([a vendor implementation of the 2009 state machine, read as the source](https://documentation.iconics.com/v10.98/Content/Alarming/Alarm%20Server/Alarm%20References/alarm-state-transition-diagram.htm)).

In scope: this product has an alarm engine, a live list and a journal, and an operator acts on all three.

**Implemented, with the evidence.**

- **The states exist and are derived from an append-only journal.** `AlarmState` is Active,
  Acknowledged, Cleared or Shelved; the live list is rebuilt from `alarm_event` at startup, so a
  standing alarm and its acknowledgement survive a restart ([ADR-0013](decisions/0013-alarm-journal.md);
  `src/Core/Alarms/AlarmEngine.cs`, `tests/Persistence.Tests/AlarmJournalTests.cs`).
- **Acknowledgement is attributed and audited**, naming the person ([ADR-0011](decisions/0011-permissions-model.md),
  `tests/Gateway.Tests/AlarmJournalHostTests.cs`).
- **The flapping remedies are real, and the reasoning is worth reading.** An **on-delay** (the condition
  must hold for it; a condition that stops inside the window raises nothing at all) and a **deadband**
  (clearing only, so an alarm still raises at exactly its limit) — both nullable, and **null is not
  zero** ([ADR-0025](decisions/0025-an-alarm-waits-before-it-announces-itself.md),
  `tests/Core.Tests/AlarmEngineTests.cs`).
- **Shelving is bounded and expires on its own**: `MaxShelveDuration` defaults to 24 hours, a shelf
  outside the allowed range is refused rather than clamped, and an expired shelf returns the alarm to
  Active with the acknowledgement cleared (`AlarmEngine.ShelveAsync`, `ExpireShelvesAsync`).
- **An outage reads as an outage**: evaluation start and stop are journalled as engine events, and a
  Bad reading never clears an alarm, because a Bad reading has no value to compare (ADR-0003).
- **A hole in the journal is recorded as a hole**, and a lost window is reported with its count and both
  ends rather than silently disappearing (ADR-0017; `tests/EdgeAgent.Tests/LossReportTests.cs`).

**Not implemented, named.** This is the largest functional gap in the document after 62443-4-1, and it
is a *feature* gap rather than a hardening one:

1. **Alarm priority does not exist.** `AlarmDefinition` carries a high limit, a low limit, an on-delay
   and a deadband — and no priority or class. Without priority there is no rationalisation (nothing to
   decide *whether* an alarm earns its place), no flood management (nothing to sort by when twenty
   arrive together), and no escalation (nothing to escalate). **Cost: a schema, API and client slice,
   and it needs an ADR** — priority changes what an alarm *is*, which is precisely the kind of change
   ADR-0025 had to make before flapping could be built.
2. **The state model is coarser than the standard's.** Return-to-normal-unacknowledged, latched states,
   and the suppressed-by-design and out-of-service states have no equivalent: an alarm that clears while
   unacknowledged becomes `Cleared` and stays listed, which is the *outcome* an operator needs, but
   "cleared but not yet seen" is not a state anything can query. **Cost: medium**, and it interacts with
   priority (a non-latched, low-priority alarm does not need the distinction; a latched one does).
3. **No suppression by design, and no first-out or cause-and-effect grouping.** Nothing can say "these
   six alarms are one event". **Cost: medium to large**, and it is usually built on priority and on
   equipment relationships the data model does not currently hold.
4. **No alarm system performance monitoring.** The standard's core metrics — alarms per hour per
   operator, percentage of time in flood, stale alarms, chattering alarms — are computable from the
   journal and **nothing computes them**. The journal exists and is queryable, so this is reporting
   rather than capture. **Cost: a reporting slice** (and the reporting module is deferred in
   `phase-plan.md`'s "Later (not yet scoped)").
5. **No notification or escalation channel at all.** This is one of `phase-0-architecture.md`'s
   "Explicitly open" items: alarms are announced on a screen and nowhere else — no email, SMS, or
   external system. A plant that relies on an operator watching a browser has no alarm system outside
   shift hours, and **that is a decision, not an oversight** — it is written down as open, and it needs
   an ADR when a deployment needs it.

### 4.3 ISA-95 / IEC 62264 — **out of scope**

**What it is.** The enterprise-control integration standard (its IEC twin is IEC 62264): terminology,
hierarchy models — including the equipment hierarchy of enterprise, site, area, work centre and work
unit — and object models for exchanging information between business systems and manufacturing
operations ([Wikipedia's overview of the parts](https://en.wikipedia.org/wiki/ANSI/ISA-95)). Out of
scope: this product has no MES or ERP interface, no B2MML, no production-schedule or work-order concept,
and no OPC UA companion-specification mapping to ISA-95 objects.

**The finding worth recording.** Our hierarchy is **Tenant → Site → Device → Tag** (ADR-0001, ADR-0004),
and it is *not* ISA-95's. The words "site" and "enterprise" overlap, which makes it easy for a customer
to assume a mapping exists; it does not, and no ADR says either way. If an integration is ever wanted,
the honest first step is a decision about whether we are modelling ISA-95 levels or merely reusing two
of its words.

### 4.4 ISA-88 — **out of scope**

**What it is.** The batch control standard: physical and procedural models, recipes, and the
equipment-phase concept that batch automation is built on (ISA-88's own part 1 is ANSI/ISA-88.00.01-2010,
[cited from the standard's listing](http://www.isa.org/Template.cfm?Section=Standards2&template=Ecommerce/FileDisplay.cfm&ProductID=11384&file=ACFA0E3.pdf);
the name and its family were learned from [ISA-95's cross-reference to it](https://en.wikipedia.org/wiki/ISA-95)).
Out of scope: this product does not control batches, does not hold recipes, and its only nod in that
direction is that a tag value may be **text** — which ADR-0003 names explicitly as "batch ID, recipe
name, operator ID". Recording that is worth more than it looks: the model can *carry* batch data
without pretending to be a batch system.

### 4.5 IEC 61131-3 — **out of scope**

**What it is.** The standard for programmable controller programming languages; the current edition is
IEC 61131-3:2025 ([the standard's listing](https://www.boutique.afnor.org/en-gb/standard/iec-6113132025/programmable-controllers-part-3-programming-languages/xs304903/444755)).
Out of scope, and this one is worth stating as a **refusal rather than an absence**: this product does not
execute user control logic at all. There is no IEC 61131-3 runtime, no ladder, no function block diagram,
and no scripting engine — the last is deferred in `phase-plan.md`'s "Later (not yet scoped)" with the
note that it waits for a phase that creates a concrete need. The consequence a customer must hear: **the
Gateway scans, judges and records; it does not control.** Control stays in the PLC, which is where both
this standard and IEC 61511 expect it.

### 4.6 ISA-5.1 and ISO 14617 — **Partial: the drawings were designed, not looked up**

**What they are.** `ANSI/ISA-5.1` (current edition **2024**) is the convention a plant engineer is
trained on for instrument symbols and tagging on a P&ID
([ANSI's announcement](https://blog.ansi.org/ansi-isa-5-1-2024-instrumentation-symbols/)); **ISO 14617**
is the international multi-part series for graphical symbols on technical diagrams, and the two are
named together as the primary sources for P&ID symbols
([Project Materials' overview](https://blog.projectmaterials.com/quick-answers/epc/common-pid-symbols/)).
**I read neither** — both are paywalled — and the searching done for this entry **did not return the
pump glyph**, which is recorded deliberately: this entry says a standard exists and was not consulted,
and it does **not** say what the standard requires.

**Why it is in scope.** ADR-0027 added symbols — a pump, a motor, a valve, a tank — and they are drawn
from first principles. The reasoning in that ADR is about *state* and *quality*, both of which it gets
right; **it contains no reference to any symbol standard at all.** The pump is four thin arms in a
circle, which reads as an impeller or a fan; the valve is a bowtie, which is the one that *is* the
industry shape; the motor is a circle lettered M.

**The finding is not "the pump is wrong".** It is that nobody looked. A reader of this repository on
2026-10-07 described the pump as *"something that symbolises a pump"* — an honest reaction, and a
reaction is what we have instead of a check. `open-work.md` §3 carries the question (*"what a pump
should look like"*) as **a decision deferred by the owner on aesthetic grounds**, and the point of this
entry is that it is **not purely an aesthetic question**: recognisability on a plant display is a
functional property, and an established convention is what makes a symbol recognisable to somebody who
has never seen *this* product before.

**One nuance from the sources, and it is the useful one.** An EPC project normally ships a **legend
sheet** defining the symbols used on its own P&IDs, and that legend governs where the two standards
differ ([Project Materials](https://blog.projectmaterials.com/quick-answers/epc/common-pid-symbols/)).
So the industry's own answer to *"whose symbol set?"* is **per-deployment**, which maps exactly onto
ADR-0027 §6's position that a second drawing is *"a drawing and a state list, not a decision"*. The
cheap, standard-compatible answer is therefore not *"redraw the pump"* but *"let a deployment choose its
symbol set"* — shipping a default that matches ISA-5.1 rather than one invented here.

**Cost: buy ISA-5.1-2024, read the pump, valve and motor glyphs, and compare — half a day.** It may
confirm what is drawn. The current state is that nobody knows.

### 4.7 IEC 60073 — **Partial, and red is doing two jobs**

**What it is.** `IEC 60073`, *Basic and safety principles for man-machine interface — coding principles
for indicators and actuators*: the rules for what a colour on an indicator is **allowed to mean**. It is
a basic safety publication under IEC Guide 104; the 2002 edition appears to be current
([GlobalSpec's listing](https://reference.globalspec.com/ref/3879665/iec-60073-2002)), and the 1991
edition was superseded in 1996
([AFNOR's record](https://www.boutique.afnor.org/en-gb/standard/iec-600731991/coding-of-indicating-devices-and-actuators-by-colours-and-supplementary-mea/xs302166/436178)).
**I did not read it.** The meanings below come from a Schneider Electric FAQ citing IEC 60204-1 for
machine signalling ([Schneider's FAQ](https://www.se.com/be/en/faqs/FA146188)) — a neighbouring standard
rather than this one. The convention is consistent across them, and no clause is quoted for that reason.

| Colour | Meaning, as the convention has it |
|---|---|
| **Red** | emergency — immediate action required |
| **Yellow / amber** | abnormal — monitoring and/or intervention |
| **Green** | normal — and its use is *optional* |
| **Blue** | an action the operator must carry out |
| **White** | neutral / monitoring, optional |

**What matches, and it matches well.** The palette is Good green, Uncertain and Stale amber, Bad red, no
reading grey; it is tokenised in `src/styles.css` with no raw colour anywhere in `src/app`; and the
**accent is deliberately hue-less** so that no colour on the screen means two things. That decision was
taken on 2026-10-06 for exactly the reason this standard exists, and reached by argument rather than by
looking it up — which is the pattern this whole row is about.

**The gap, and it is real.** **Red is used for two different things that the convention separates.**

- `--status-bad-bg: #fbe6e6` / `--status-bad-ink: #9b2c25` means *this reading cannot be trusted*.
- `--alarm-surface: #fff6f5` / `--alarm-border: #f0c7c4` means *a limit has been crossed*.

Two near-identical reds, one screen. Under the convention, an **alarm** is red's actual meaning — a
condition demanding action. A **Bad reading is not an emergency**; it is "I do not know", which is much
closer to amber's *abnormal, needs monitoring or intervention*, and arguably closer still to **no colour
at all**. The present arrangement spends the loudest signal on the condition that demands the least
immediate action, and then distinguishes the two by a shade a reader cannot name.

**This is not a defect to patch into the palette.** ADR-0003's four qualities are a decision, and so is
whatever red means here; changing it is a decision of the same weight and belongs in an ADR, beside
ADR-0027's rule that **quality overrides state**. Recorded here as the finding.

**One more thing the convention says that we do**: green is *optional*, and a badge saying everything is
fine on every tile is how a reader learns to stop reading badges. That argument is already written down
in `out-of-range.test.mjs` for a different marker; it applies to the Good pill too, and has not been
asked about.

### 4.8 OPC UA Part 9 — **Partial: the industry already has an alarm model, and ours is close**

**What it is.** `OPC 10000-9` / `IEC 62541-9`, *Alarms and Conditions* — the alarm model of the one
protocol this product already speaks. Current release **1.05.06 (2025-10-31)**
([the OPC Foundation's reference](https://reference.opcfoundation.org/Core/Part9/v105/docs/)). It is a
published reference rather than a paywall, and the definitions below are quoted from it; where a concept
is named without a definition, that section was not read and it says so.

**What we match, having arrived separately — including one rule exactly.** Part 9's glossary defines
shelving as temporarily preventing an alarm from being displayed *"when it is causing the Operator a
nuisance"*, offers **OneShotShelving and TimedShelving**, and carries a duration property whose stated
purpose is to *"prevent permanent Shelving of an Alarm"*. **ADR-0013 requires a capped expiry chosen from
a fixed list** — the same rule, reasoned out here from the same hazard, and now shown to be the
industry's own answer rather than a local preference. Part 9's **Active** state (*"An Alarm is Standing
whilst the Condition persists"*) is our Active, and its separation of Active from the acknowledgement
states is the separation ADR-0013 draws.

**The two gaps.**

- **`Severity`.** Part 9 carries a severity on a condition. We have none — which is §9's third gap seen
  from the protocol side rather than from ISA-18.2's. **That it is missing against two independent
  standards is the strongest argument yet that it is missing from the product**, and whichever ADR closes
  it should take Part 9's severity and ISA-18.2's priority classes together rather than inventing a third
  scheme.
- **Confirm.** Part 9's acknowledgeable-condition model has **Acknowledge *and* Confirm** as separate
  acts. Ours has acknowledgement only. The distinction — generally understood as *an operator has seen
  it* versus *the cause has been dealt with* — is one this product cannot currently express, so an alarm
  that was acknowledged and an alarm that was actually fixed look alike. **§4.3 was not read**, so this
  describes the concept and not the standard's state machine.

**Not in scope, and worth saying:** none of this means the driver should *expose* Part 9 over OPC UA —
§2.1 already records that the driver polls and does not subscribe, and reading a server's alarms is a
separate feature nobody has asked for. **This entry is about borrowing the model, not the wire.**

---

## 5. Regulated industries

### 5.1 21 CFR Part 11 — **out of scope by decision, with the hard half already built**

**What it is.** The US FDA's regulation on electronic records and electronic signatures in
predicate-rule industries, requiring among other things that closed systems ensure the authenticity,
integrity and, where appropriate, confidentiality of electronic records, and that they use **secure,
computer-generated, time-stamped audit trails** that record creation, modification and deletion without
obscuring previously recorded information, and that the trail be available for review and copying
(§11.10(e); see [AWS's restatement of §11.10(e) in its compliance guidance](https://docs.aws.amazon.com/config/latest/developerguide/operational-best-practices-for-FDA-21CFR-Part-11.html)).
Out of scope by decision: `phase-0-architecture.md`'s "Explicitly open" says **"Any regulatory/compliance
module — none is assumed; added only if and when a real deployment needs one."**

**What already satisfies part of it, which is why the refusal is cheap to reverse.** An append-only
`audit_log` whose application role has `INSERT`/`SELECT` and nothing else, attributing every
security-relevant write to a user id with a UTC timestamp, enforced at the database and tested over the
application's own connection (ADR-0011; `tests/Persistence.Tests/AuditLogAppendOnlyTests.cs`) — that is
§11.10(e)'s hard half. Unique per-user accounts, a length-based password rule, session expiry and
revocation are the access-control half.

**What is missing.** (a) **An electronic signature**: there is no signing act, no identity
re-verification at the moment of signing, no signature manifestation (name, date and meaning), and no
record that a signature cannot be excised. (b) **Audit trail review and copying** — see the IEC 62443
entry, item 2: the trail cannot be read through the product at all. (c) **Retention and archival
policy**, which is a decision nobody has taken. (d) **Validation documentation** — the "validation"
that Part 11's scope assumes lives in the quality system, and would be GAMP 5's job, below.
**None of this should be built without a customer who needs it**; what is worth doing now is the audit
read path (which every industry wants) and saying in the guide that the trail is append-only and
tamper-resistant to the application.

### 5.2 GAMP 5 — **out of scope**

**What it is.** ISPE's *Good Automated Manufacturing Practice* guide, whose second edition (2022) is the
pharma industry's framework for computerised system validation and risk-based compliance
([an industry guide to GAMP 5 for medical devices](https://meddeviceguide.com/blog/gamp-5-computerized-system-validation-medical-device-guide),
[ISPE's own presentation material](https://www.ispeboston.org/download/educational_presentations/2024/2024-Product-Show-Suite-20-1400PM-GAMP.pdf)).
Out of scope for the same reason as Part 11, and it would *follow* Part 11 rather than lead it: GAMP 5 is
how a regulated manufacturer validates a system, and it is their document, not the supplier's. What a
supplier contributes is what this repository already produces in abundance — written decisions, a test
record with mutation evidence, a deployment guide with a tested rollback — and item 3 of the IEC 62443
entry is where the missing supplier-side discipline actually lives.

---

## 6. Time, and measurement

### 6.1 ISO 8601 and time handling — **Implemented, with one named gap**

**What it is.** The international standard for representing dates and times for interchange: ISO
8601-1:2019 and -2:2019, with a 2022 amendment, covering calendar dates, 24-hour times, UTC offsets,
durations and intervals ([Wikipedia's description, including the current editions](https://en.wikipedia.org/wiki/ISO_8601)).
It matters here more than for a typical web product, because a SCADA's whole output is a claim about
*when* something was true.

**Implemented, and this is the part of the repository that most deserves its own paragraph.** ADR-0003
puts a **source timestamp and an ingestion timestamp** on every reading, distinct by design, and accepts
out-of-order source times and stores them at their true point rather than at arrival. ADR-0017 §5 states
that **the edge's clock is neither trusted nor overwritten**: a skew beyond a configured tolerance
(30 seconds by default, `src/Gateway/Scanning/PushedSourceSettings.cs`) is *journalled* as a
`SourceClockSkew` event carrying how far and which way (`src/Core/Tags/PushedSourceRecorder.cs`,
`AlarmEventType.SourceClockSkew`), and the samples keep the times the source gave them. The two-host
walk made this real rather than theoretical: a staged +45 s appeared in full as a
`clock_skew_seconds = 46.8458066` journal row on the Gateway's clock while 136 samples were stored with
the edge's times, and the walk's own caveat is recorded honestly — neither host had a working NTP source
(`open-work.md` §1.2, `phase-7-manual-gate.md`). Timestamps are `timestamptz`, values are UTC internally,
and the trend axis dates itself when a window crosses midnight (ADR-0029's walk).

**The gap, and it is a display gap with a real consequence.** `site.time_zone_id` exists (an IANA zone,
default `UTC`) and travels all the way to the client in `SiteDto` — and **nothing displays with it.**
Every time the client prints is the *browser's* local clock: `getHours()` in `models.ts` (the gap window
and the axis), `screen.ts` (the staleness note), and `toLocaleString` for one helper. So an operator
whose workstation is set to the wrong zone reads every timestamp shifted, and a site in one zone viewed
from another shows times that belong to the viewer. `phase-0-architecture.md` states the intent —
"UTC internally, local time zone only at display" — and calls it "not yet its own ADR… a working
assumption to formalize if it needs to become binding". **Cost: a small client change** (format against
the Site's zone) **plus a decision** about which zone wins when the reader and the plant disagree — which
is exactly what the missing ADR would settle. The related deployment gap is that **no guide requires a
time source**: nothing says the Gateway host should run NTP, even though the whole skew story assumes the
Gateway's own clock is right.

### 6.2 IEC 61000-4-30 — **out of scope**

**What it is.** The electromagnetic-compatibility standard for *power quality measurement* methods and
instrument accuracy, currently 4th edition (2025), with the well-known class distinction between the
most accurate class used for contractual and standard-compliance measurements and the lighter class used
for surveys ([the standard's listing](https://www.boutique.afnor.org/en-gb/standard/iec-610004302025/electromagnetic-compatibility-emc-part-430-testing-and-measurement-techniqu/xs305216/451939);
I did not read the standard, so the class definitions above are described from general practice rather
than verified clause text). Out of scope: this product is not a measurement instrument. It displays what
a device reported, at the device's own source time, with the device's quality — and it must never be
presented as a compliant power-quality instrument, because the accuracy claim belongs to the meter.

**Why it earns a line anyway.** If a deployment puts power-quality values on a screen here, a reader may
reasonably assume the numbers are trustworthy in that standard's sense. ADR-0003's discipline (a value
carries its quality and its source time, and a Bad reading carries no value) is the product's honest
answer to that, and it is worth saying out loud in the customer's own terms: *this is the meter's number,
shown; it is not our measurement.*

---

## 7. Forward-looking

### 7.1 OPC UA FX and IEC/IEEE 60802 — **not implemented, no decision yet**

**What they are.** OPC UA FX (*Field eXchange*) is the OPC Foundation's work on field-level OPC UA
between controllers and devices, including the PubSub and discovery work needed to replace vendor buses;
IEC/IEEE 60802 is the joint profile that defines a time-sensitive-networking (TSN) profile for industrial
automation, and the two efforts are deliberately aligned
([IEEE 802.1's 60802 material on the OPC Foundation's field-level initiative](https://www.ieee802.org/1/files/public/docs2026/60802-Enzinger-FLC-Update-0526-v02.pdf);
[a further 802.1 contribution from the OPC field-level communications initiative](https://grouper.ieee.org/groups/802/1/files/public/docs2021/60802-Hummen-OPC-FLC-contribution-0321-v01.pdf)).
Neither is a standard this product must meet today. It is listed because two of this project's
decisions sit right beside them: ADR-0017 chose *our own payload* over Sparkplug B for the edge link, and
ADR-0016's pushing-driver contract is a half-step toward field-level pub/sub. **If a customer's roadmap
says FX, the honest answer today is no** — and, as with every entry here, that refusal is not currently
recorded anywhere, which is what this document is for.

---

## 8. Functional safety — **out of scope by decision, and it is the most important refusal here**

### 8.1 IEC 61508 — **out of scope by decision**

**What it is.** The umbrella functional-safety standard for electrical, electronic and programmable
electronic safety-related systems: a safety lifecycle, safety-integrity levels (SIL 1–4), hardware
architectural constraints, diagnostic coverage and technique tables, and requirements on the
development process itself ([the standard's part 1 listing](https://webstore.ansi.org/standards/iec/iec61508ed1998)).
Out of scope, and **not because the effort has not been made yet**: a SIL is a property of a
*safety function* implemented by a system developed under that lifecycle — with a safety requirements
specification, a hazard analysis, verification and validation evidence, a functional safety assessment,
and management of functional safety competence. **This product has none of that and is not trying to
have it.** No claim of any SIL may be made for it, by us or by a deployment built on it.

**What it does contribute to a safety picture, without pretending to be one.** ADR-0003's rule that a
Bad reading carries **no value** rather than a substituted one, and that a value carries its quality and
its true source time, is the property a safety case needs from a *supervisory* system: it never says a
number that was not measured. The same rule reappears at every layer here — a trend draws a hole rather
than a ramp (ADR-0029's walk), an alarm never clears on a Bad reading, a silent pushing source goes Bad
rather than holding a cached value (ADR-0016), and ambiguous states carry a reason rather than nothing
(ADR-0021). Those are the honest-reporting properties of a monitoring system, and they are why a plant's
safety case can cite what it sees here *as monitoring* while the protection layer stays in the SIS.

### 8.2 IEC 61511 — **out of scope by decision**

**What it is.** The process-industry sector application of IEC 61508, *Functional safety — Safety
instrumented systems for the process industry sector* (part 1 is the framework, definitions, system,
hardware and application programming requirements; IEC 61511-1:2016 with amendment 1 of 2017:
[the IEC webstore entry](https://webstore.iec.ch/en/publication/24241),
[the ANSI preview of the amended part 1](https://webstore.ansi.org/preview-pages/IEC/preview_iec61511-1%7Bed2.1%7Db.pdf)).
It governs the *safety instrumented system* — the independent protection layer with its own sensors,
logic and final elements — and the safety lifecycle that produces it. Out of scope: a SCADA is a
different layer. It supervises, it alarms, it records; it does not trip a plant.

**The rule this document should leave behind, in plain words.** *Nothing built on this product may be
the protection layer of a process.* If a deployment's risk assessment says an interlock is needed, the
interlock belongs in a SIS engineered under IEC 61511, and this product may at most be *told* that it
happened. The direction of that information matters and is already in the code: the Gateway records what
a device reports, refuses to invent a value, and never treats its own software as trustworthy evidence
of plant state. A future "compliance module" is not planned — `phase-0-architecture.md`'s "Explicitly
open" says a regulatory module is added only when a real deployment needs one — and if one is ever
built, **it must not be a safety claim**.

---

## 9. The biggest gaps, with their cost

Ranked by what a serious reviewer would raise first, not by effort.

*This section was called "the three biggest gaps" until 2026-10-08 and now carries four. The fourth is
not a fourth defect — it is the pattern the other three were found by, and it was added when rows
23–29 made it visible.*

**1. The secure development lifecycle (IEC 62443-4-1) — and since 2026-10-08 it has a legal
deadline against it.** No CI, no build-time dependency scanning, no SBOM, no vulnerability intake or
disclosure path, no threat model, no security-requirements register, no signed releases.

**The EU Cyber Resilience Act requires most of this list by law** for a product with digital elements
sold into the EU (§3.6): **actively exploited vulnerabilities reported to ENISA within 24 hours from
11 September 2026**, and conformity assessment with CE marking from 11 December 2027. That changes
nothing technically — the same artefacts close both — and it changes the ordering, because the
earliest-dated piece is also the cheapest: **a published security contact and a written process is an
afternoon**, not a pipeline. This is the widest gap in the document and the only one that is about *how the product
is made* rather than what it does. **Cost: weeks, mostly process; needs an owner decision** (who reviews
a security report, who signs a release, whether a pipeline is built here or in another repository). The
cheap first steps exist already as manual measurements — the two dependency audits in `open-work.md` §4 —
and turning them into a pipeline is a day's work; the rest is not.

**2. OPC UA security (IEC 62541 Part 2).** The driver connects to plant devices with `useSecurity: false`
and an anonymous identity. Every other transport here is protected: the web surface by ADR-0028, the edge
link by ADR-0017. ~~**No ADR records that decision**~~ — *corrected 2026-10-08*: the ADR this entry asked
for is [**ADR-0033**](decisions/0033-the-opc-ua-driver-negotiates-security.md), and it was right that one
was needed — deciding what a deployment trusts is a decision, not a setting. **The gap itself is still
open**: the decision is in force and the code is unchanged, which are two different claims and the reason
this entry stays in the list. **Remaining cost: a week, plus the deployment-facing certificate story.**

**3. Alarm priority (ISA-18.2 — and OPC UA Part 9's `Severity`, which is the same gap found twice).**
There is no priority or class on an alarm definition, so alarms cannot be rationalised, sorted, flooded,
escalated or measured — five of the standard's central practices, all of which rest on that one field.
**§4.8, added 2026-10-08, finds the same field missing against a second and independent standard**, which
is the strongest evidence in this document that it is missing from the product rather than merely absent
from one standard's checklist. Whichever ADR closes it should take both vocabularies together rather
than inventing a third. Part 9 also has **Confirm** as an act distinct from Acknowledge, which this
product cannot express at all. The engine itself is in better shape than most of this document
(states, on-delay, deadband, bounded shelving, an append-only journal, mutation-verified). **Cost: a
schema, API, client and reporting slice; needs an ADR**, for the same reason ADR-0025 did: priority
changes what an alarm *is*.

**4. Three decisions were taken by taste where a standard already existed (added 2026-10-08).** Not a
gap in the product so much as a gap in how it is being built, and it is the reason rows 23–29 exist:

- **what an equipment symbol looks like** — ISA-5.1 and ISO 14617 (§4.6), never consulted when ADR-0027
  drew a pump, a motor, a valve and a tank;
- **what a colour is allowed to mean** — IEC 60073 (§4.7), which the palette happens to match, except
  that **red is doing two jobs**: Bad quality and an active alarm, in two near-identical reds, where the
  convention reserves red for the condition demanding action;
- **what an alarm is** — OPC UA Part 9 (§4.8), whose shelving cap ADR-0013 reinvented exactly and whose
  `Severity` and `Confirm` we lack.

**Cost: low for each, and the habit is worth more than the three fixes.** The owner's rule, stated
2026-10-08, is that an established standard decides a question before we do. This document is where that
rule is cashed out, and **the first pass of it could not have caught these** — it asked which standards
this code meets, not which standards exist for the questions the code answered on its own.

**The cheapest high-value fixes, for contrast** — this list was written on 2026-10-07 and **three of its six
were built the same day**, which is the point of writing costs down: brute-force protection on login (gap 1
under IEC 62443, ADR-0031), an audit-trail read path (gap 2, ADR-0032), and a CSP with clickjacking headers
(gap 7, ADR-0031 §8 — including `script-src` by hash, which the ADR first deferred and then corrected).
**Two of the three needed an ADR after all**, which is recorded as a correction in §3.1 items 1 and 2. What
remains of the list: an NTP requirement in the deployment guide (§6.1), an SBOM step in the build (gap 4),
and correcting the Sparkplug B claims (§2.1, which turned out to be five mentions in three documents rather
than one sentence).

---

## 10. What I could not verify, and how this document should be kept honest

Written into the record rather than left in a tool's scrollback:

- **I read no standard in full.** Every one cited above is paywalled or was read through a public
  summary, a normative annex published online, or an implementation of it. Where I named a requirement
  instead of a clause number, that was deliberate: **a wrong clause number is worse than none**, because
  it sends a reader to the wrong page of a document they then have to buy.
- **IEC 62443-4-2's requirement numbering is not quoted** anywhere in this document. The requirements are
  named (unique identity, account management, authenticator strength, session integrity, authorisation,
  audit, communication integrity, PKI, and so on) because those are the twelve subject areas the
  [series overview](https://en.wikipedia.org/wiki/IEC_62443) describes, and the individual SR numbers
  were not read.
- **ISA-101.01's text was not read**, so its requirements are described at the level of philosophy,
  style guide, toolkit, lifecycle and audit — the structure secondary sources agree on — rather than by
  clause. The display-hierarchy convention is attributed to industry practice, not to the standard.
- **IEC 61000-4-30's class definitions were not read**; the class distinction is described from general
  practice.
- **For rows 23–29, added 2026-10-08, none of the paywalled documents was read**: not ISA-112.00.01-2025,
  not ISA-5.1-2024, not ISO 14617, not IEC 60073, and not Regulation (EU) 2024/2847. Each entry says so
  in its own text, and each names the secondary source it is judged against.
- **The ISA-5.1 pump glyph was searched for and not found.** §4.6 therefore records that a standard
  exists and was not consulted; it deliberately does **not** state what the standard requires, because
  that would be the wrong-clause-number failure in a different costume.
- **The Sparkplug 3.0 specification text was not retrieved** — only its version page. §2.7's description
  of birth and death certificates and of `STATE` is from widely-agreed secondary descriptions and is
  named rather than quoted.
- **IEC 60073's own colour table was not retrieved.** §4.7's table is from a Schneider Electric FAQ
  citing IEC 60204-1, a neighbouring standard; the conventions agree, and that is why no clause is
  quoted.
- **The CRA dates are from two law-firm summaries**, not from EUR-Lex, and **nothing found addresses how
  the CRA classifies SCADA software specifically** — whether it falls in a higher-risk annex is open, has
  a real cost attached, and is a question for a lawyer rather than for this document.
- **NERC CIP sub-requirement numbering was not read**, and none is used.
- **OPC DA's `QQSSSSLL` structure** is quoted from OPC UA Part 8's own normative annex, which is a
  published source, rather than from the OPC DA specification itself.
- **No third party has assessed this product**, and no conformity is claimed. The status column is an
  internal reading of code against public descriptions of standards; it is evidence of care, not of
  compliance.
- **A decision landed while this document was written, and it is now in the tree.** While this audit read
  the code, [ADR-0030](decisions/0030-a-tag-declares-the-range-it-expects.md) was being written: a tag may
  declare the range its readings are expected in, a reading outside it keeps its value and its reported
  quality and is marked beside them, and null means nothing was declared. **It is on `main` and its
  server half is in a pull request**; what is **not built is the half that shows it on a screen** — the
  tag form's two fields and the marker beside a reading. The gap it answers is the one `open-work.md`
  §2.0l item 2 has carried since 2026-10-06 (*nothing marks a value that cannot be true — a percentage
  above 100 is drawn exactly like a pressure*), which is where a reader looking for it will find it; it
  is **not** recorded in §4.1 of this document, and the first draft of this paragraph said it was — a
  wrong cross-reference inside an audit whose own rule is that a wrong clause number is worse than none,
  corrected here because it was checked. It lands on the three gaps named above in this way: §2.1's OPC UA
  limit information and §2.2/§2.3's out-of-range bits are now **carried by this product's own model** for
  tags whose range an operator declares, but a range arriving *from* an OPC UA server's `EURange` is still
  not read (recorded as an open item in the ADR), and IEC 61850 remains out of scope. **Those entries were
  amended here rather than left as they stood**, which is the rule at the end of this document applied to
  the one case that arose while it was being written.

**When this document goes stale, and who fixes it.** A status here is a claim about the present tense,
which is the failure this repository has paid for most often: the audit becomes a document that says
"IEC 62541 Part 2 is not implemented" a month after it is. The rule is the same one the ADRs carry —
**the session that closes a gap amends the sentence in the same pull request** — and this file should be
re-read whenever a phase closes, exactly as `open-work.md` is. It is a register, not a source of truth;
where it disagrees with an ADR, the ADR wins and this file is wrong.
