-- ADR-0025: an alarm waits before it announces itself, and a deadband shifts where it clears.
--
-- Two settings, nullable, and both NULL means the alarm behaves exactly as it did before this
-- migration. That is the important property: this cannot change what an existing alarm means, or
-- every deployment would silently acquire a different alarm system on upgrade.
--
-- `on_delay_seconds` is a wait. The condition must hold for it before the alarm is raised, and a
-- condition that stops holding inside the window raises nothing at all -- no journal row, and
-- nothing an operator has to dismiss again. That is what the Phase 5.5 walk recorded: one Site
-- writing three journal rows every ~25 seconds while a value oscillated across a limit.
--
-- `deadband` is how far a value must come back PAST the limit before the alarm clears. It applies
-- to clearing only, never to raising (ADR-0025 section 3), so an operator reading "High limit
-- 4.50 bar" is reading the truth about when the alarm will go off.

ALTER TABLE alarm_definition
    ADD COLUMN on_delay_seconds double precision,
    ADD COLUMN deadband double precision;

-- Bounded rather than merely non-negative, because both settings trade something real away and an
-- unbounded one is a way to build an alarm that never raises. An on-delay longer than an hour is a
-- suppression, and suppression is what shelving is for (ADR-0013); a deadband wider than the whole
-- span between the two limits would mean a High alarm that can never clear at all.
ALTER TABLE alarm_definition
    ADD CONSTRAINT ck_alarm_definition_on_delay
        CHECK (on_delay_seconds IS NULL OR (on_delay_seconds > 0 AND on_delay_seconds <= 3600));

ALTER TABLE alarm_definition
    ADD CONSTRAINT ck_alarm_definition_deadband
        CHECK (deadband IS NULL OR deadband > 0);

-- Recreated to carry the two new columns, the way migration 0007 recreated it over the new
-- tag_active. The view names its columns explicitly, so a column added to the table is not visible
-- through it until it is added here -- and a read that silently omits an alarm's deadband would
-- evaluate it with no deadband at all, which is the one failure this whole ADR is about.
DROP VIEW alarm_definition_active;

CREATE VIEW alarm_definition_active AS
SELECT a.id, a.tag_id, a.high_limit, a.low_limit, a.on_delay_seconds, a.deadband
FROM alarm_definition a
JOIN tag_active t ON t.id = a.tag_id
WHERE a.deleted_at IS NULL;
