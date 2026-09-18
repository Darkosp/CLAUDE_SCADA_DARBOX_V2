-- The alarm journal (ADR-0013): an append-only record of every alarm transition and of
-- when the engine was and was not evaluating. It is the source of truth for alarm
-- state; the live list is rebuilt from it at startup, and there is no second table of
-- "current alarms" to drift from it.

-- Grants are opt-in from here on (ADR-0013). Migration 0008 made every later table
-- writable by the application role by default, restoring append-only tables by
-- revoking afterwards; forgetting that revoke would destroy a guarantee without a
-- sound, while forgetting a grant fails loudly the first time anything writes. So new
-- tables now get SELECT and INSERT only, and one that genuinely needs UPDATE or DELETE
-- grants them in the migration that creates it. This comes first, so that the table
-- below is created under the new default. Tables that already exist keep their grants.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    REVOKE UPDATE, DELETE ON TABLES FROM scada_app;

CREATE TABLE alarm_event (
    id                     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,

    event_type             text NOT NULL,

    -- The alarm this event belongs to: a Raised starts an occurrence, every later event
    -- of that alarm repeats it, and Retired closes it. Engine-level events belong to no
    -- alarm and carry none.
    occurrence_id          uuid,

    -- Foreign keys to the base tables, not the active views: history outlives
    -- configuration (ADR-0001, ADR-0009), and an event about a since-deleted tag must
    -- still be insertable — its retirement, for one.
    definition_id          uuid REFERENCES alarm_definition (id),
    tag_id                 uuid REFERENCES tag (id),

    -- Denormalised onto the row, not resolved through tag -> device -> site, because
    -- that path breaks for soft-deleted equipment and alarm history for retired
    -- equipment is exactly what a review needs.
    site_id                uuid REFERENCES site (id),

    -- Two clocks, as tag_sample keeps (ADR-0003): when the plant produced the reading,
    -- and when the engine recorded the event. Engine-level events have no source time;
    -- nothing at the plant produced them.
    source_time            timestamptz,
    recorded_at            timestamptz NOT NULL,

    -- Who acted, where anyone did: the stable id, and the name as it read then —
    -- users are renamed and deactivated, and a Viewer cannot resolve an id (ADR-0011).
    -- No foreign key, as in audit_log: the journal records what happened regardless
    -- of what later becomes of the account.
    actor_user_id          uuid,
    actor_username         text,

    alarm_limit            text,
    limit_value            double precision,
    value                  double precision,
    unit_symbol            text,

    -- The display path as the operator saw it at that moment. Presentation, never
    -- identity (ADR-0001).
    tag_path               text,

    -- Seen on the first evaluation after a restart, in either direction: not observed
    -- happening, only observed to be so.
    detected_after_restart boolean NOT NULL DEFAULT false,

    shelved_until          timestamptz,

    -- Why, where the type alone does not say: a retirement caused by the definition
    -- being deleted rather than by an acknowledgement or a recovery.
    reason                 text,

    -- A period nothing was recorded. On EvaluationStarted it runs from the last moment
    -- the Gateway is known to have been alive; on JournalGap it is the window during
    -- which journal writes were failing.
    gap_from               timestamptz,
    gap_to                 timestamptz,

    CONSTRAINT ck_alarm_event_type CHECK (event_type IN (
        'Raised', 'Acknowledged', 'Shelved', 'Unshelved', 'Cleared', 'Retired',
        'EvaluationStarted', 'EvaluationStopped', 'JournalGap')),

    -- An event about one alarm names that alarm fully; an event about the engine names
    -- none of it. A half-populated row would be an event that belongs nowhere, or one
    -- that the Site filter could not place.
    CONSTRAINT ck_alarm_event_belongs CHECK (
        CASE WHEN event_type IN ('EvaluationStarted', 'EvaluationStopped', 'JournalGap')
            THEN occurrence_id IS NULL AND definition_id IS NULL AND tag_id IS NULL AND site_id IS NULL
            ELSE occurrence_id IS NOT NULL AND definition_id IS NOT NULL AND tag_id IS NOT NULL AND site_id IS NOT NULL
        END),

    -- A shelf always ends (ADR-0013); an indefinite one is not representable.
    CONSTRAINT ck_alarm_event_shelf_expires CHECK (
        event_type <> 'Shelved' OR shelved_until IS NOT NULL),

    CONSTRAINT ck_alarm_event_limit CHECK (alarm_limit IS NULL OR alarm_limit IN ('High', 'Low')),

    CONSTRAINT ck_alarm_event_actor_named CHECK (
        (actor_user_id IS NULL) = (actor_username IS NULL)),

    CONSTRAINT ck_alarm_event_gap_ordered CHECK (
        gap_from IS NULL OR gap_to IS NULL OR gap_from <= gap_to)
);

-- Rebuilding the live list at startup: every event of the occurrences not yet retired.
CREATE INDEX ix_alarm_event_occurrence ON alarm_event (occurrence_id, id);
CREATE INDEX ix_alarm_event_retired ON alarm_event (occurrence_id) WHERE event_type = 'Retired';

-- Reading the journal for the Sites a caller may see, newest first.
CREATE INDEX ix_alarm_event_site_time ON alarm_event (site_id, recorded_at DESC);

-- Append-only at the database level (ADR-0013), exactly as audit_log. The default set
-- above already withholds UPDATE and DELETE; granting and revoking explicitly as well
-- means the table's posture is stated here, not left to an ordering detail two
-- statements earlier.
GRANT SELECT, INSERT ON alarm_event TO scada_app;
REVOKE UPDATE, DELETE, TRUNCATE ON alarm_event FROM scada_app;
