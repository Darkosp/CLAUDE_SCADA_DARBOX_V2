-- ADR-0034: an alarm carries a priority, and an alarm nobody has rationalised says so.
--
-- **The important property of this migration is what it does NOT do: it backfills nothing.**
--
-- ISA-18.2 and IEC 62682 make priority a product of *rationalisation* -- somebody assesses the
-- consequence of ignoring an alarm and the time an operator has to respond. NULL therefore means
-- "not yet rationalised", which is a real stage of the standard's own lifecycle rather than an
-- absence of ours. The same shape as ADR-0025's null on-delay and ADR-0030's undeclared range:
-- **null is not zero**, and this is the third time this project has had to say so.
--
-- Every available default would have been a lie:
--
--   'High'    makes a deployment 100% high priority, which the standard says is the same as having
--             no priorities at all -- if everything stands out, nothing does.
--   'Low'     silently downgrades alarms that may matter, on upgrade, with nobody told.
--   'Medium'  asserts a middling consequence for alarms nobody has assessed.
--
-- All three are the product deciding something only the plant can. Phase 6.5's upgrade renamed a
-- duplicate that was **really there**; there is no equivalent here, because the right value is not
-- knowable from the data. So every existing alarm becomes "not yet rationalised", which is accurate.

ALTER TABLE alarm_definition
    ADD COLUMN priority text;

-- The values, in the database as well as in the model. Spelled out rather than left to the
-- application because a row written by a future tool, a script or a migration has to mean the same
-- thing as a row written by the API -- the "a rule and every place that applies it have to move
-- together" lesson, in the one place that can enforce it.
--
-- **The null case is written out rather than left to the comparison.** A CHECK rejects only `false`,
-- and `NULL IN ('High', ...)` is `unknown`, so the constraint would already admit NULL -- but this
-- repository has been bitten by exactly that reasoning being left implicit (migration 0009's own
-- constraint), and a reader must not have to work out whether the author knew.
ALTER TABLE alarm_definition
    ADD CONSTRAINT ck_alarm_definition_priority
    CHECK (priority IS NULL OR priority IN ('High', 'Medium', 'Low'));

-- And on the journal, because the ordering has to survive a restart.
--
-- The live list is rebuilt from this journal at startup (ADR-0013), so without this column a
-- standing alarm would come back as *not yet rationalised* and the screen would silently reorder
-- itself after every restart. That is worse than it sounds: the reordering would be invisible as a
-- defect and would look like the alarms themselves had changed.
--
-- It also keeps the journal honest about **what the operator was actually shown**. An alarm raised
-- as High and re-rationalised to Low while still standing was a High alarm when somebody was asked
-- to deal with it, and the history has to agree with the screen they were looking at -- the same
-- reasoning ADR-0025 section 7 gives for carrying the deadband and the wait on the alarm.
ALTER TABLE alarm_event
    ADD COLUMN priority text;

ALTER TABLE alarm_event
    ADD CONSTRAINT ck_alarm_event_priority
    CHECK (priority IS NULL OR priority IN ('High', 'Medium', 'Low'));

-- And the view, which is the half of this that a test caught and reading would not have.
--
-- `alarm_definition_active` (ADR-0009's soft-delete view) **names its columns**, so a column added to
-- the table underneath is invisible to every read -- and every read of a threshold goes through this
-- view. Without this the column exists, the writes succeed, and `SELECT ... priority FROM
-- alarm_definition_active` fails with `42703: column "priority" does not exist`, which reads like the
-- migration never ran.
--
-- Migration 0019 left a note saying exactly this, in those words, and it was still missed here. **It
-- is the "a rule and every place that applies it have to move together" shape**, and the place that
-- catches it is the suite rather than the reviewer: ninety-four Gateway tests went red at once.
DROP VIEW alarm_definition_active;

CREATE VIEW alarm_definition_active AS
SELECT a.id, a.tag_id, a.high_limit, a.low_limit, a.on_delay_seconds, a.deadband, a.priority
FROM alarm_definition a
JOIN tag_active t ON t.id = a.tag_id
WHERE a.deleted_at IS NULL;

-- A view dropped and recreated is a NEW object, so the grant does not survive (migration 0017's
-- lesson, repeated in 0020). Without this every alarm evaluation fails with a permission error whose
-- cause is three files away.
GRANT SELECT ON alarm_definition_active TO scada_app;
