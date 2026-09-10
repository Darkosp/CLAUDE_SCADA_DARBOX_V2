-- Alarm thresholds (Phase 3).
--
-- Its own table rather than columns on tag: a tag may later carry more than one
-- condition (HighHigh/High/Low/LowLow) and a separate table extends without reshaping
-- tag. The foreign key is to the base table, matching every other reference in the
-- schema; reads go through the active view.
CREATE TABLE alarm_definition (
    id          uuid PRIMARY KEY,
    tag_id      uuid NOT NULL REFERENCES tag (id),
    high_limit  double precision,
    low_limit   double precision,
    deleted_at  timestamptz,

    -- A definition that watches for nothing is a configuration mistake, not a valid
    -- row: it would sit in the UI looking like an alarm while never being able to fire.
    CONSTRAINT ck_alarm_definition_has_a_limit
        CHECK (high_limit IS NOT NULL OR low_limit IS NOT NULL)
);

-- A definition is only live while its tag is (ADR-0009's implementation note): the tag
-- owns it, so a deleted tag must not leave a threshold behind that still evaluates.
CREATE VIEW alarm_definition_active AS
SELECT a.id, a.tag_id, a.high_limit, a.low_limit
FROM alarm_definition a
JOIN tag_active t ON t.id = a.tag_id
WHERE a.deleted_at IS NULL;

CREATE INDEX ix_alarm_definition_active ON alarm_definition (tag_id) WHERE deleted_at IS NULL;
