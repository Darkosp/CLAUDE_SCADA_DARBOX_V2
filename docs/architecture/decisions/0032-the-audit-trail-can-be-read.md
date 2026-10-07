# ADR-0032 — The audit trail can be read

**Status:** Accepted
**Date:** 2026-10-07

## Context

**The audit trail has been written since Phase 5 and read by nobody.** `audit_log` is append-only, `UPDATE`,
`DELETE` and `TRUNCATE` are revoked from the application's own role (migration 0008), and it carries the
things a real investigation needs: who signed in and who failed, which alarm was acknowledged and by whom,
which write reached a plant, which edge configuration was published, which user's role changed. **There is
no endpoint and no screen.** The standards audit (`docs/architecture/standards-baseline.md` §3.1) found it in
one line: *"an audit trail that cannot be read is evidence nobody can produce"*.

**And it is the half that is cheap to lose.** Every session that adds a feature adds rows to this table —
ADR-0031 added two actions in one morning. The rows accumulate, the table is indexed only by
`occurred_at`, and nothing in the product has ever asked it a question. A trail that is written but never
read is a trail whose shape nothing tests: the first reader is the first time anybody finds out whether the
detail written in 2025 is enough to answer a question asked in 2026.

## Decision

**1. The trail is readable, by an Admin, and by nobody else.**

An audit row carries **no Site**, and that is by design (ADR-0011): a failed sign-in may name no account at
all, and a Gateway-wide event belongs to no Site by construction. So there is no Site filter over this table
that could be honest — a Site-scoped reader would either be shown other Sites' rows or shown a trail with
holes in it, and **a trail with holes is worse than no trail**, because it looks complete. Admin is the only
tenant-wide role the model has (ADR-0004), so Admin is the only role that can read it without a lie.

**2. It is read backwards by `id`, not by time.**

`occurred_at` is **not unique** and cannot be made so: ADR-0031's lock writes a failed-sign-in row and a lock
row in the same millisecond, and the same is true of any operation that changes several things at once.
Paging a trail ordered by a non-unique column either repeats a row at a page boundary or drops one — the
exact defect where a reader cannot tell a missing entry from one that never existed. The order is
`id DESC`, the cursor is the last `id` a reader saw (`before`), and "load older" is therefore exact and
offset-free. `id` is a `GENERATED ALWAYS AS IDENTITY` column, so it is also the writing order, which is what
a reviewer means by "what happened next".

**3. A page says how many rows match, so a short page cannot pass for a whole trail.**

The journal's own lesson, learned in ADR-0025's work: *a limit that nobody mentions looks like a quiet
night*. Every page carries `total` beside its entries, and the screen says *"the newest 200 of 4,312"* when
that is the truth. This costs a second query on an admin-only screen that is opened by hand; it buys the one
property an investigation cannot do without, which is knowing whether you are looking at everything.

**4. Looking at the trail does not write to it.**

A read is not journalled, deliberately. **An audit trail that grows when somebody looks at it is a trail an
attacker can bury their own entries in** — and burying an entry is the one thing the append-only design
exists to make impossible. The reader is an Admin, whose every *write* is already journalled; that an Admin
can also read is not itself a change to the system. This is argued rather than assumed, because "log the
reads" is the instinctive answer.

**5. The actor is resolved at read time, and from `app_user` rather than `app_user_active`.**

A row stores the actor's id. The page shows a name, joined live — so a name is spelled the way it is spelled
today rather than frozen at write time, and **a deactivated account still reads as the person it was**
instead of a bare GUID. Reading through `app_user_active` (ADR-0009) would make an ex-employee's actions
look like nobody's, which is the opposite of what an investigation needs.

**6. `detail` comes back as opaque JSON and is never parsed into columns.**

The writer's contract is an opaque document (ADR-0011) precisely so that adding a field to an action does
not need a migration. Parsing it at read time would put the trail's *shape* in the reader, where it would
drift from the writers — and the entries that stop meaning what they meant are always the old ones. The
screen prints it as text.

**7. Filters narrow, and an unreadable one is refused by name.**

An action **prefix** (`auth.` asks for the family, because actions are dotted stable names), an actor, an
entity, and a time window. A filter the server cannot parse is refused with a message naming it rather than
ignored (the journal's rule in ADR-0025): silently dropping a filter answers a **wider** question than the
one asked, and shows the reader rows they will read as having matched it.

**8. The page is capped exactly as the journal's is**, 200 by default and 1000 at most. Two screens asking
the same server for "the newest N rows" should not disagree about what N may be.

## Consequences

- **No migration.** The table, its index and its revocations already exist; this adds a reader and a screen.
  The query filters on `action` and `actor_user_id`, which have no index — acceptable while the table is read
  by hand, and named here so that the first slow query is not a mystery. A partial index on
  `occurred_at` per action is the obvious fix when it is needed.
- **The count is a second query over the filtered set.** It is the price of §3, paid on a screen that is
  opened deliberately rather than polled.
- **The client screen is part of this decision, not a later slice.** A trail reachable only by `curl` is the
  shape this project has already named as a defect — *the work exists and nothing on screen points at it* —
  so the slice is not done until an Admin can read it in the product.
- **What this does not do**: it does not make the trail tamper-evident (a database superuser can still
  rewrite it, and nothing here would notice), it does not export it, and it does not alert on anything in it.
  Those are separate decisions, and the first one is the one a regulated deployment would actually ask for.

## Verified in review by

- The newest rows come back first, and **two rows written in the same millisecond both appear** — the case
  that proves the order is not by time.
- A page says how many rows match, and `total` counts the whole filtered set rather than the page.
- `before` returns strictly older rows and never repeats or skips one at a boundary.
- An `action` prefix matches the family (`auth.`) and not an unrelated action that merely contains it.
- An actor's name is shown, **and a deactivated actor's name is still shown**.
- A reader who is not an Admin is refused, and the refusal does not reveal what the trail contains.
- An unparseable filter is refused by name; the window and the cap behave as the journal's do.
