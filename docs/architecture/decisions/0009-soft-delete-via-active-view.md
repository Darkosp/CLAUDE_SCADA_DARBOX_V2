# ADR-0009 — Soft delete via active-row view for configuration entities

**Status:** Accepted
**Date:** 2026-09-09

## Context

Phase 2 added Folder/Device/Tag CRUD through the UI, which raises a
question the phase's test gate didn't require but its own existence now
forces: what does deleting one of these config rows actually do?

ADR-0001 already decided that historized data outlives its config —
`tag_sample` deliberately carries no foreign key to `tag`, so a tag's
history remains queryable by ID regardless of what happens to the tag's
configuration row. A **hard delete** would honor that at the storage
level but defeat its purpose in practice: a trend chart for a device
retired a year ago would resolve to a bare UUID instead of "Header
Pressure", which is a real cost for a platform whose value is
multi-year historian data.

A **soft delete** (a `deleted_at` column, filtered out of normal reads)
keeps the name, but introduces a standing obligation: every current and
future query against these tables — list, get-by-id, the browse tree,
TagCatalog hot-add, autocomplete, a future report — must remember to
filter `WHERE deleted_at IS NULL`. A forgotten filter doesn't fail loudly;
a deleted Device or Tag would just quietly reappear as if still live. This
is the same class of defect Phase 2 already produced twice by accident
(the trend chart fabricating a line through a data gap; a form field
silently shadowing `HTMLFormElement.tagName`): quiet, incorrect
information presented as correct. A review checklist and test-coverage
requirement can catch this for code that exists today, but pays out
forever — every future author has to remember, and nothing stops a new
query from being written without the filter before a test catches it.

Three options were weighed: hard delete; soft delete relying on
convention plus a review/test requirement; and soft delete with the
filter enforced structurally. The third follows the same reasoning
already applied to the Folder/Device Site-scoping invariant: make the
wrong thing impossible to reach by the normal path, rather than relying
on every future author remembering a rule.

## Decision

Folder, Device, and Tag each gain a `deleted_at timestamptz null`
column on their existing base table, added by a new numbered DbUp
migration. Each base table gets a matching read-only view —
`folder_active`, `device_active`, `tag_active` — defined as
`SELECT * FROM <table> WHERE deleted_at IS NULL`.

All read paths used for browsing, CRUD listing, the tree API
(`GET /api/sites/{siteId}/tree`), TagCatalog/hot-add, and any future
lookup UI query the `_active` view — never the base table directly.
The only code paths that touch the base table directly are: the delete
operation itself (an `UPDATE` setting `deleted_at`), and historian
queries resolving a name for old samples (see below). Foreign keys,
including the composite Site-scoped FKs already in place for
Folder/Device, continue to reference the base tables unchanged — this
ADR adds a read-side filter, not a storage or constraint change.

A Folder can be deleted only when it has no non-deleted child Folder or
Device (checked against `folder_active`/`device_active`); the API
rejects the delete otherwise; there is no implicit cascade and no
automatic reparenting. This matches ADR-0001 §6: a structural position
in the browse tree must never change as a side effect of something else
— an operator moves or deletes contents explicitly, one action at a
time.

Deleting a Device cascades to soft-deleting its owned Tags in the same
operation. This is deliberately different from the Folder rule: a Tag is
not an independently placed entity the way a Device is (ADR-0001 §3 —
Device "owns" its tags), so there is no meaningful way to "reparent" a
Tag away from its Device, and no reason to force a separate manual step
for something that isn't really a choice.

Historian rows are unaffected. ADR-0001's decision that historized data
outlives its config stands exactly as before; a query resolving a name
for a soft-deleted Device's or Tag's historical samples joins against
the base table (not the `_active` view), so old data still displays a
name instead of a bare ID.

Site and Tenant deletion are explicitly out of scope for this ADR —
deleting a Site means decommissioning part of an installation and
deleting the sole Tenant (ADR-0004) means decommissioning the instance;
neither is a UI CRUD action, and both stay unaddressed until a concrete
need raises them separately.

## Consequences

Forgetting the active-row filter becomes structurally hard for the
normal path — a new repository method that queries `device` instead of
`device_active` is a visible, grep-able mistake rather than a silent one
a reviewer has to notice by inference. The cost: one additional
migration per soft-deletable table, a view that must be recreated
alongside any future migration that changes that table's shape, and a
delete code path that is intentionally the one place allowed to bypass
the view.

## Verified in review by

- Every read path for Folder/Device/Tag other than the delete operation
  itself queries `folder_active`/`device_active`/`tag_active`, never the
  base table.
- A test asserts a soft-deleted row does not appear via any repository
  read method (list, get-by-id, tree, TagCatalog hot-add).
- Deleting a Folder with a non-deleted child Folder or Device fails, with
  no partial side effect.
- Deleting a Device soft-deletes its owned Tags in the same transaction.
- A historian query for a soft-deleted Device's or Tag's samples still
  resolves and displays its name.
