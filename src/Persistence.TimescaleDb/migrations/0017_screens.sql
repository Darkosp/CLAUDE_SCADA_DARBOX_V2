-- Operator screens (ADR-0024): a screen is configuration, not code.
--
-- Two tables, because a screen and the things on it have different lifetimes: an author adds a
-- component to a screen far more often than they make one, and deleting a component must not take
-- its screen with it.
--
-- Deleting is soft, like every other configuration table (ADR-0009), so an audit row that names a
-- screen still resolves a name. Each table gets an _active view and every read path goes through it.

CREATE TABLE screen (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL REFERENCES tenant (id),
    site_id    uuid NOT NULL REFERENCES site (id),
    name       text NOT NULL,
    position   integer NOT NULL DEFAULT 0,
    deleted_at timestamptz
);

CREATE TABLE screen_component (
    id           uuid PRIMARY KEY,
    site_id      uuid NOT NULL,
    screen_id    uuid NOT NULL,
    row_index    integer NOT NULL DEFAULT 0,
    column_span  integer NOT NULL DEFAULT 12,
    position     integer NOT NULL DEFAULT 0,
    kind         text NOT NULL,
    title        text,
    tag_id       uuid,
    deleted_at   timestamptz
);

-- The composite keys that let a reference carry its site along with it, the same shape folder and
-- device already use (0003, 0012). Cross-site placement is a security boundary, so it is made
-- unrepresentable by a key rather than refused by repository code: a check that lives only in
-- application code is bypassable by any future path that writes these tables.
ALTER TABLE site
    ADD CONSTRAINT uq_site_tenant_id UNIQUE (tenant_id, id);

ALTER TABLE screen
    ADD CONSTRAINT uq_screen_site_id UNIQUE (site_id, id);

-- A screen's tenant is its site's tenant.
ALTER TABLE screen
    ADD CONSTRAINT fk_screen_site_same_tenant
    FOREIGN KEY (tenant_id, site_id) REFERENCES site (tenant_id, id);

-- A component's site is its screen's site. site_id is carried on the component rather than derived
-- from the screen so that the tag reference below can be checked against it by the database; it is
-- not something a caller supplies, and the repository writes it from the screen it is saving.
ALTER TABLE screen_component
    ADD CONSTRAINT fk_component_screen_same_site
    FOREIGN KEY (site_id, screen_id) REFERENCES screen (site_id, id);

-- A tag's site has to be reachable as a key, and a tag does not carry one: its device does
-- (migration 0001). So a component that names a tag names its *device* too, and the site reaches the
-- tag through two keys rather than one:
--
--   screen_component (site_id, device_id) -> device (site_id, id)
--   screen_component (device_id, tag_id)  -> tag    (device_id, id)
--
-- Together they make a tag on another site unrepresentable: the first pins the device to the
-- component's site, the second pins the tag to that device. One key could not do it, because a tag
-- has no site of its own to compare against -- and the same two-step is what 0003 used to let a
-- folder's parent reference carry its site.
--
-- Adding site_id to the tag table instead would have been the smaller change, and it is refused
-- because tag_active names its columns (migration 0004): a new column means recreating that view,
-- and the reason to pay that is a column something reads. Nothing reads a tag's site; it is only
-- ever a check.
ALTER TABLE device
    ADD CONSTRAINT uq_device_site_id UNIQUE (site_id, id);

ALTER TABLE tag
    ADD CONSTRAINT uq_tag_device_id UNIQUE (device_id, id);

ALTER TABLE screen_component
    ADD COLUMN device_id uuid;

-- A component's device must be on the same site as the component, and its tag must be on that
-- device. Either key alone lets a cross-site reference through; the pair does not.
--
-- When device_id or tag_id is NULL the corresponding constraint is not checked at all, which is
-- what makes the kinds that read no tag legal -- the default MATCH SIMPLE semantics that 0003
-- relies on for a root folder.
ALTER TABLE screen_component
    ADD CONSTRAINT fk_component_device_same_site
    FOREIGN KEY (site_id, device_id) REFERENCES device (site_id, id);

ALTER TABLE screen_component
    ADD CONSTRAINT fk_component_tag_on_device
    FOREIGN KEY (device_id, tag_id) REFERENCES tag (device_id, id);

-- A name is unique within its site, among live rows, ignoring case (ADR-0015). No NULLS NOT
-- DISTINCT here: unlike a folder's parent, a screen's site is never null.
CREATE UNIQUE INDEX ux_screen_name_in_site
    ON screen (site_id, lower(name))
    WHERE deleted_at IS NULL;

-- The columns are listed rather than SELECT *, for the reason 0004 gives: a view created with *
-- fixes its column list, so a later migration adding a column would leave the view silently missing
-- it. Naming them makes that a build-time concern instead.
CREATE VIEW screen_active AS
SELECT id, tenant_id, site_id, name, position
FROM screen
WHERE deleted_at IS NULL;

CREATE VIEW screen_component_active AS
SELECT id, site_id, screen_id, row_index, column_span, position, kind, title, tag_id, device_id
FROM screen_component
WHERE deleted_at IS NULL;

-- Grants, for the reason 0009 gives: since that migration a new table is granted SELECT and INSERT
-- only, and one that genuinely needs more asks for it in the migration that creates it.
--
-- A screen is soft-deleted (ADR-0009), which is an UPDATE, and it is edited far more often than it
-- is made. A component does not need UPDATE: a screen is saved by replacing its whole component set,
-- and the repository does that with a DELETE and an INSERT rather than by marking rows deleted --
-- a component has no name for a soft-deleted row to preserve, and a soft-deleted row keeps its
-- primary key, so re-inserting a component that kept its id would collide with it.
--
-- Missing this is not a subtle failure and it is worth naming what it looked like: the first run of
-- the screen tests failed with `42501: permission denied for table screen_component` on the delete,
-- while every read worked. Reads are exactly what the default grant covers.
GRANT SELECT, INSERT, UPDATE ON screen TO scada_app;
GRANT SELECT, INSERT, DELETE ON screen_component TO scada_app;

-- The views are read by the repository, and a view is a separate object with its own privileges:
-- granting on the table does not grant on the view over it. SELECT alone, because every write goes
-- to the tables by name -- a view is a read path (ADR-0009).
GRANT SELECT ON screen_active TO scada_app;
GRANT SELECT ON screen_component_active TO scada_app;
