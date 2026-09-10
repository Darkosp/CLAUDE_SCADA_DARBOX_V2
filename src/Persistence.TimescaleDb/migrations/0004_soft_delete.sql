-- Soft delete for configuration entities (ADR-0009).
--
-- A deleted row stays in its base table so that a historian sample recorded years ago
-- can still resolve a name rather than a bare UUID. What makes that affordable is that
-- the filter is not left to every future author to remember: each table gets an _active
-- view, and every read path goes through it. A query written against the base table by
-- mistake is then a grep-able error, not a silent one.

ALTER TABLE folder ADD COLUMN deleted_at timestamptz;
ALTER TABLE device ADD COLUMN deleted_at timestamptz;
ALTER TABLE tag    ADD COLUMN deleted_at timestamptz;

-- The columns are deliberately listed rather than SELECT *: a view created with * fixes
-- its column list at creation time, so a later migration that adds a column to the base
-- table would leave the view silently missing it. Naming them makes that a build-time
-- concern instead — this view has to be updated alongside such a migration, and the
-- explicit list is what makes that obvious to whoever writes it.
CREATE VIEW folder_active AS
SELECT id, site_id, parent_folder_id, name
FROM folder
WHERE deleted_at IS NULL;

CREATE VIEW device_active AS
SELECT id, site_id, folder_id, name, driver_key, connection_settings, scan_interval_ms
FROM device
WHERE deleted_at IS NULL;

CREATE VIEW tag_active AS
SELECT id, device_id, name, value_kind, unit_symbol, unit_dimension,
       unit_factor_to_si, unit_offset_to_si, source_address, is_writable
FROM tag
WHERE deleted_at IS NULL;

-- Reads are dominated by "the live configuration", so index for that shape.
CREATE INDEX ix_folder_active ON folder (site_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_device_active ON device (site_id) WHERE deleted_at IS NULL;
CREATE INDEX ix_tag_active ON tag (device_id) WHERE deleted_at IS NULL;
