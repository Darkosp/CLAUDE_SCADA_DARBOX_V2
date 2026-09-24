# ADR-0015 — A name is unique within its parent, among live rows

**Status:** Accepted
**Complements:** ADR-0001 (identity is the stable id; the path is a display
label) and ADR-0009 (soft delete through an active-row view). Neither is
superseded.
**Date:** 2026-09-24

## Context

Sending the same create request twice creates two devices. Nothing prevents
it: the id is generated server-side on every request, there is no uniqueness
anywhere in the schema for `device.name`, `tag.name` or `folder.name`, and
there is no idempotency key. The same holds for tags and folders.

This surfaced while writing a script against the API during Phase 6 — the
first attempt reported an error to its caller *after* the server had already
created the device, and the retry made a second one. The script's bug is not
the interesting part. What matters is that the interesting case needs no bug
at all: an operator double-clicks Save, or a reply is lost on a flaky link and
they press it again, and the plant now has two devices called `Booster 4`.

Two identical names under one parent is worse here than it looks, because
ADR-0001 made the hierarchical path a *display label* — and the display is
where people work. The alarm banner, the journal and the trend all identify a
tag as `Skopje/Pump House/Discharge Pressure`. With duplicates that string no
longer answers "which one", and an operator at four in the morning is reading
exactly that string. Meanwhile half the duplicate pair is scanning a device
nobody is watching.

The obvious fixes are not equal:

- **Disabling Save while the request is in flight** stops the double click and
  nothing else. A retry after a lost response still duplicates.
- **An idempotency key** solves retries properly, but it asks every client to
  carry one, and it does nothing about two different people creating the same
  name a minute apart — which is the case that actually happens in a plant with
  two engineers.
- **Uniqueness in the database** answers all three, in the one place no code
  path can bypass.

## Decision

**A name is unique within its parent, among rows that are not deleted, and the
comparison ignores case.**

- A device's parent is its folder within its site; a device directly under the
  site (`folder_id IS NULL`) counts as a parent of its own.
- A tag's parent is its device.
- A folder's parent is its parent folder within its site, with root folders
  (`parent_folder_id IS NULL`) again counting as one parent.

Enforced by partial unique indexes over the base tables — `WHERE deleted_at IS
NULL`, so a deleted row does not hold its name for ever (ADR-0009 keeps it in
the table so that an old sample can still resolve a name; it must not keep it
in the namespace). Case is folded with `lower(name)` in the index, because
`Pump House` and `pump house` are the same thing to the person reading the
banner.

**`NULLS NOT DISTINCT`**, or an equivalent that makes two null parents collide.
This is the trap that CLAUDE.md already records in its other direction: under
the default `NULLS DISTINCT`, every row with `folder_id IS NULL` is unique to
Postgres, so the constraint would silently do nothing for exactly the devices
that sit directly under a Site — the common case in a small installation.
PostgreSQL 17 (ADR-0006) supports `NULLS NOT DISTINCT` directly.

**The API answers 409**, with a message naming what already exists, and the
client shows it against the name field rather than as a bare failure.

**Existing duplicates are renamed, not dropped, and the rename is audited.**
An upgrade must not fail on data someone already has, and it must not quietly
change what an operator sees with no trace: the migration suffixes all but one
of each colliding set and writes one `audit_log` entry per rename (ADR-0011's
append-only trail, actor null — nobody did it).

*Corrected while implementing (2026-09-24):* this first said the **later**
rows are renamed. No row records when it was created, so "later" is not a
thing the database can answer. The row with the smallest id keeps the name —
arbitrary but deterministic — and the audit entry carries both names, so an
operator who wanted the other one can swap them. The suffix is the whole id,
not a prefix of it: with a prefix, two renamed devices came out with the same
name in testing, because seeded ids share their leading characters.

**Idempotency keys stay out of scope**, and so does anything that makes the
client responsible for the guarantee. Disabling Save while a request is in
flight is still worth doing as a courtesy, but it is not the guarantee and must
not be described as one.

## Consequences

- Two devices with the same name under one parent become impossible, whatever
  the cause: a double click, a retry, or two people.
- A retry after a lost response now gets 409 rather than a second device. That
  is a better answer than a duplicate, but it is not the same as "the first
  request succeeded" — the client cannot tell the two apart, and that stays
  true until there is an idempotency key.
- Renaming a device to a name a *deleted* device once had is allowed, which is
  the point of the partial index, and means the path in an old journal row may
  now match a different live entity. Ids, not paths, are what history is
  resolved by (ADR-0001), so this changes nothing that is load-bearing.
- One more migration, and one more way an upgrade can surprise someone — with
  an audited rename, rather than a refusal to start.

## Verified in review by

- There is a test, run over the application's own connection, in which the
  same create request is issued twice and the second is refused with 409;
  removing the index makes it create a second row instead.
- There is a test for the null parent — two devices with the same name
  directly under one Site, and two root folders with the same name in one
  Site. Under the default `NULLS DISTINCT` this test fails, which is the whole
  reason it exists.
- There is a test that the comparison ignores case, and one that the same name
  under a *different* parent is still allowed.
- There is a test that a name freed by a soft delete can be used again.
- The migration is tested against a database that already contains duplicates:
  it renames rather than fails, every rename appears in `audit_log`, and the
  index exists afterwards.
