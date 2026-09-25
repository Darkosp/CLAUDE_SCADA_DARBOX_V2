-- The cloud side of the edge link (ADR-0017): ingestion that tolerates the same sample twice,
-- and two journal entries a pushing source can cause — samples it had to drop, and a clock
-- that disagrees with the Gateway's.

-- 1. Idempotent ingestion per (tag, source timestamp), for pushed samples.
--
-- The link is at-least-once: a batch the broker received but whose acknowledgement the edge
-- never saw is sent again, and the Gateway has to store it once. The key is scoped to samples
-- that arrived pushed, deliberately not the whole table. A polled OPC UA tag whose value has not
-- changed is read with the same source timestamp scan after scan, and those rows exist today;
-- a table-wide key would need a migration that deletes history and would change the polled path,
-- which this step does not touch.
--
-- The column has a constant default and is NOT NULL, so every existing row is simply "not
-- pushed" and the unique index — partial on it — has nothing to say about them.
ALTER TABLE tag_sample ADD COLUMN pushed boolean NOT NULL DEFAULT false;

-- Includes source_time, the hypertable's partitioning column, as TimescaleDB requires of a
-- unique index. Ingestion names this predicate in its ON CONFLICT target.
CREATE UNIQUE INDEX ux_tag_sample_pushed ON tag_sample (tag_id, source_time) WHERE pushed;

-- 2. Journal entries about a pushing source.
--
-- Neither belongs to an alarm, and neither to the engine as a whole: each is about one device,
-- which is on one Site, and is filtered to that Site's readers like an alarm event.
ALTER TABLE alarm_event
    ADD COLUMN device_id uuid REFERENCES device (id),
    -- SamplesLost: how many samples the source dropped, and the id the source gave that loss.
    -- The same report can arrive twice (at-least-once), so the id is what makes it one entry.
    ADD COLUMN lost_samples bigint,
    ADD COLUMN loss_id uuid,
    -- SourceClockSkew: the source's clock minus the Gateway's, at the moment the Gateway received
    -- the message. source_time holds the source's reading and recorded_at the Gateway's; neither
    -- is corrected, and no sample's time is.
    ADD COLUMN clock_skew_seconds double precision;

ALTER TABLE alarm_event DROP CONSTRAINT ck_alarm_event_type;
ALTER TABLE alarm_event ADD CONSTRAINT ck_alarm_event_type CHECK (event_type IN (
    'Raised', 'Acknowledged', 'Shelved', 'Unshelved', 'Cleared', 'Retired',
    'EvaluationStarted', 'EvaluationStopped', 'JournalGap',
    'SamplesLost', 'SourceClockSkew'));

-- Three kinds of row now: about an alarm, about the engine, about a source. Each names exactly
-- what it belongs to. A source event has a Site, so the journal's Site filter places it; it has
-- no occurrence, so rebuilding the live list at startup never sees it.
ALTER TABLE alarm_event DROP CONSTRAINT ck_alarm_event_belongs;
ALTER TABLE alarm_event ADD CONSTRAINT ck_alarm_event_belongs CHECK (
    CASE
        WHEN event_type IN ('EvaluationStarted', 'EvaluationStopped', 'JournalGap')
            THEN occurrence_id IS NULL AND definition_id IS NULL AND tag_id IS NULL
                 AND site_id IS NULL AND device_id IS NULL
        WHEN event_type IN ('SamplesLost', 'SourceClockSkew')
            THEN occurrence_id IS NULL AND definition_id IS NULL AND tag_id IS NULL
                 AND site_id IS NOT NULL AND device_id IS NOT NULL
        ELSE occurrence_id IS NOT NULL AND definition_id IS NOT NULL AND tag_id IS NOT NULL
             AND site_id IS NOT NULL AND device_id IS NULL
    END);

-- A loss says how much and when, or it is not a stated gap. Every field is required with IS NOT
-- NULL as well as compared: "NULL >= 1" is unknown, not false, and a CHECK only rejects false —
-- the defect migration 0009's own constraint once had.
ALTER TABLE alarm_event ADD CONSTRAINT ck_alarm_event_samples_lost CHECK (
    CASE WHEN event_type = 'SamplesLost'
        THEN lost_samples IS NOT NULL AND lost_samples >= 1 AND loss_id IS NOT NULL
             AND gap_from IS NOT NULL AND gap_until IS NOT NULL
        ELSE lost_samples IS NULL AND loss_id IS NULL
    END);

ALTER TABLE alarm_event ADD CONSTRAINT ck_alarm_event_clock_skew CHECK (
    CASE WHEN event_type = 'SourceClockSkew'
        THEN clock_skew_seconds IS NOT NULL AND source_time IS NOT NULL
        ELSE clock_skew_seconds IS NULL
    END);

-- One entry per loss, however often it is reported. Null loss ids — every other row — are
-- distinct from each other, which is what is wanted here, unlike ADR-0015's names.
CREATE UNIQUE INDEX ux_alarm_event_loss ON alarm_event (loss_id);

-- Reading a device's source events.
CREATE INDEX ix_alarm_event_device_time ON alarm_event (device_id, recorded_at DESC) WHERE device_id IS NOT NULL;
