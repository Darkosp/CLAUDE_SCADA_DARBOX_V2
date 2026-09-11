-- Device templates (UDTs), per ADR-0010.
--
-- A template is a live reference, not a stamp: editing it changes every instance. What
-- it cannot do is share tags between instances — ADR-0001 requires every tag to own a
-- stable ID so its history stays continuous, so instantiation materialises real tag rows
-- and this schema only records where each one came from.

-- Templates are reusable across sites, which is the point of having them, so they hang
-- off the tenant rather than a site (ADR-0004).
CREATE TABLE device_template (
    id         uuid PRIMARY KEY,
    tenant_id  uuid NOT NULL REFERENCES tenant (id),
    name       text NOT NULL,
    deleted_at timestamptz
);

-- The shape of one tag on the template. address_template carries named placeholders
-- (for example 'holding:{offset}?scale=0.01'); what those resolve to is a driver's
-- business, not this table's (ADR-0002).
CREATE TABLE device_template_tag (
    id                uuid PRIMARY KEY,
    template_id       uuid NOT NULL REFERENCES device_template (id),
    name              text NOT NULL,
    value_kind        smallint NOT NULL,
    unit_symbol       text,
    unit_dimension    smallint,
    unit_factor_to_si double precision,
    unit_offset_to_si double precision,
    address_template  text NOT NULL,
    is_writable       boolean NOT NULL DEFAULT false,
    deleted_at        timestamptz
);

ALTER TABLE device ADD COLUMN template_id uuid REFERENCES device_template (id);

-- What makes three instances of one template differ. Values are opaque strings: core
-- substitutes them by name and never interprets them.
CREATE TABLE device_template_parameter (
    device_id uuid NOT NULL REFERENCES device (id),
    name      text NOT NULL,
    value     text NOT NULL,
    PRIMARY KEY (device_id, name)
);

-- Which template tag a materialised tag came from, so a later template edit can find the
-- rows it has to update. Nullable: a tag created directly on a device has no template.
ALTER TABLE tag ADD COLUMN template_tag_id uuid REFERENCES device_template_tag (id);

CREATE VIEW device_template_active AS
SELECT id, tenant_id, name
FROM device_template
WHERE deleted_at IS NULL;

-- A template owns its tags, so the same ownership rule as tag_active applies: a deleted
-- template must not leave template tags behind that still look live (ADR-0009).
CREATE VIEW device_template_tag_active AS
SELECT t.id, t.template_id, t.name, t.value_kind, t.unit_symbol, t.unit_dimension,
       t.unit_factor_to_si, t.unit_offset_to_si, t.address_template, t.is_writable
FROM device_template_tag t
JOIN device_template p ON p.id = t.template_id
WHERE t.deleted_at IS NULL
  AND p.deleted_at IS NULL;

-- Both existing views name their columns explicitly, which is exactly why adding a
-- column to a base table has to recreate them: a view built with SELECT * would have
-- silently kept the old shape. This is the maintenance cost migration 0004 predicted.
DROP VIEW device_active;

CREATE VIEW device_active AS
SELECT id, site_id, folder_id, name, driver_key, connection_settings, scan_interval_ms,
       template_id
FROM device
WHERE deleted_at IS NULL;

-- alarm_definition_active is built on tag_active, so it has to come down first and go
-- back up afterwards. Views that depend on other views make every shape change a small
-- cascade like this; it is the price of putting the liveness rule in the schema rather
-- than in every query, and it is visible here rather than surprising someone later.
DROP VIEW alarm_definition_active;

DROP VIEW tag_active;

CREATE VIEW tag_active AS
SELECT t.id, t.device_id, t.name, t.value_kind, t.unit_symbol, t.unit_dimension,
       t.unit_factor_to_si, t.unit_offset_to_si, t.source_address, t.is_writable,
       t.template_tag_id
FROM tag t
JOIN device d ON d.id = t.device_id
WHERE t.deleted_at IS NULL
  AND d.deleted_at IS NULL;

-- Recreated exactly as migration 0006 defined it, now over the new tag_active.
CREATE VIEW alarm_definition_active AS
SELECT a.id, a.tag_id, a.high_limit, a.low_limit
FROM alarm_definition a
JOIN tag_active t ON t.id = a.tag_id
WHERE a.deleted_at IS NULL;

CREATE INDEX ix_device_template_tag_active ON device_template_tag (template_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_device_by_template ON device (template_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_tag_by_template_tag ON tag (template_tag_id) WHERE deleted_at IS NULL;
