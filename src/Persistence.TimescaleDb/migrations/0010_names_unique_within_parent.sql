-- A name is unique within its parent, among live rows, ignoring case (ADR-0015).
--
-- A device's parent is its folder within its site, a tag's is its device, a folder's is its
-- parent folder within its site. Deleted rows (ADR-0009) keep their row but not their name.

-- 1. Rename what already collides, so an upgrade never fails on data someone has.
--
-- No row records when it was created, so which of two duplicates came "later" cannot be
-- known. The row with the lowest id keeps its name — arbitrary, but the same on every run —
-- and each other one gets its whole id as a suffix. Only the whole id is certain not to
-- collide: ids that share a prefix are ordinary (the demo seed's all begin 0f7a1b2c), and a
-- shortened suffix made two renamed rows identical in the test against seeded duplicates.
-- Every rename is written to audit_log with both names, so an operator who would rather have
-- kept the other one can see exactly what to swap. Nobody did it, so the actor is null.

WITH ranked AS (
    SELECT id, name,
           row_number() OVER (PARTITION BY site_id, parent_folder_id, lower(name) ORDER BY id) AS n
    FROM folder
    WHERE deleted_at IS NULL
),
renamed AS (
    UPDATE folder f
    SET name = r.name || ' (duplicate ' || r.id::text || ')'
    FROM ranked r
    WHERE f.id = r.id AND r.n > 1
    RETURNING f.id, r.name AS old_name, f.name AS new_name
)
INSERT INTO audit_log (actor_user_id, action, entity_type, entity_id, detail)
SELECT NULL, 'folder.rename_duplicate', 'folder', id,
       jsonb_build_object('from', old_name, 'to', new_name, 'reason', 'ADR-0015: names are unique within their parent')
FROM renamed;

WITH ranked AS (
    SELECT id, name,
           row_number() OVER (PARTITION BY site_id, folder_id, lower(name) ORDER BY id) AS n
    FROM device
    WHERE deleted_at IS NULL
),
renamed AS (
    UPDATE device d
    SET name = r.name || ' (duplicate ' || r.id::text || ')'
    FROM ranked r
    WHERE d.id = r.id AND r.n > 1
    RETURNING d.id, r.name AS old_name, d.name AS new_name
)
INSERT INTO audit_log (actor_user_id, action, entity_type, entity_id, detail)
SELECT NULL, 'device.rename_duplicate', 'device', id,
       jsonb_build_object('from', old_name, 'to', new_name, 'reason', 'ADR-0015: names are unique within their parent')
FROM renamed;

WITH ranked AS (
    SELECT id, name,
           row_number() OVER (PARTITION BY device_id, lower(name) ORDER BY id) AS n
    FROM tag
    WHERE deleted_at IS NULL
),
renamed AS (
    UPDATE tag t
    SET name = r.name || ' (duplicate ' || r.id::text || ')'
    FROM ranked r
    WHERE t.id = r.id AND r.n > 1
    RETURNING t.id, r.name AS old_name, t.name AS new_name
)
INSERT INTO audit_log (actor_user_id, action, entity_type, entity_id, detail)
SELECT NULL, 'tag.rename_duplicate', 'tag', id,
       jsonb_build_object('from', old_name, 'to', new_name, 'reason', 'ADR-0015: names are unique within their parent')
FROM renamed;

-- 2. The indexes.
--
-- NULLS NOT DISTINCT is the point, not a detail. A device directly under its site has
-- folder_id NULL, and a root folder has parent_folder_id NULL. Under the default (NULLS
-- DISTINCT) every such row is unique to PostgreSQL whatever its name, and the index would
-- silently exempt exactly the common case of a small installation — the three-valued-logic
-- trap CLAUDE.md records. A tag's parent is never null, so its index needs no such clause.

CREATE UNIQUE INDEX ux_folder_name_in_parent
    ON folder (site_id, parent_folder_id, lower(name)) NULLS NOT DISTINCT
    WHERE deleted_at IS NULL;

CREATE UNIQUE INDEX ux_device_name_in_parent
    ON device (site_id, folder_id, lower(name)) NULLS NOT DISTINCT
    WHERE deleted_at IS NULL;

CREATE UNIQUE INDEX ux_tag_name_in_device
    ON tag (device_id, lower(name))
    WHERE deleted_at IS NULL;
