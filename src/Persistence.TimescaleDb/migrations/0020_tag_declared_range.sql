-- A tag may declare the range its readings are expected to fall in (ADR-0030).
--
-- The range is the tag's own statement about plausibility, which is a different statement from its unit
-- (ADR-0005 says what a number means) and a different one from an alarm limit (ADR-0025 says a value
-- somebody chose to watch). Both ends or neither: a half-declared range has no verdict to give.

-- tag_active names its columns, so a new column on the table is not visible through it, and
-- alarm_definition_active is built on tag_active — it has to come down first and go back up afterwards.
-- The same small cascade migration 0007 documents; nothing else in the schema depends on tag_active.
DROP VIEW alarm_definition_active;
DROP VIEW tag_active;

ALTER TABLE tag
    ADD COLUMN range_low  double precision,
    ADD COLUMN range_high double precision;

-- Two constraints rather than one, and the pair is the point. `range_low < range_high` alone is a
-- comparison over a nullable column, and Postgres rejects only a CHECK that evaluates to false: a row
-- with one end null is `unknown`, which passes. So the pairing is its own constraint, written as a
-- comparison of two null tests, and the ordering one is guarded by an explicit IS NULL.
--
-- Refused in the schema as well as in the API, because the API is not the only thing that will ever
-- write this table (a migration, a script and a future import all reach it directly).
ALTER TABLE tag
    ADD CONSTRAINT ck_tag_range_paired CHECK ((range_low IS NULL) = (range_high IS NULL)),
    ADD CONSTRAINT ck_tag_range_ordered CHECK (range_low IS NULL OR range_low < range_high);

CREATE VIEW tag_active AS
SELECT t.id, t.device_id, t.name, t.value_kind, t.unit_symbol, t.unit_dimension,
       t.unit_factor_to_si, t.unit_offset_to_si, t.source_address, t.is_writable,
       t.template_tag_id, t.range_low, t.range_high
FROM tag t
JOIN device d ON d.id = t.device_id
WHERE t.deleted_at IS NULL
  AND d.deleted_at IS NULL;

-- Recreated exactly as migration 0018 last defined it, now over the new tag_active.
CREATE VIEW alarm_definition_active AS
SELECT a.id, a.tag_id, a.high_limit, a.low_limit, a.on_delay_seconds, a.deadband
FROM alarm_definition a
JOIN tag_active t ON t.id = a.tag_id
WHERE a.deleted_at IS NULL;

-- Grants, for the reason migration 0017 spells out: granting on the table does not grant on the view
-- over it, and a view dropped and recreated is a new object. Without this every read of a tag — and so
-- every scan, every screen and every alarm evaluation — fails with a permission error whose cause is
-- three files away. SELECT is all either view ever needs; the writes go to the tables underneath, where
-- the application role already has what it needs.
GRANT SELECT ON tag_active TO scada_app;
GRANT SELECT ON alarm_definition_active TO scada_app;
