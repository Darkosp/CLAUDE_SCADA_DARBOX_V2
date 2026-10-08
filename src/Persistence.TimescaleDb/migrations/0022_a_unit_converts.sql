-- A stored unit that cannot convert is cleared, and the clearing is recorded (ADR-0005).
--
-- **This migration exists because the refusal that came with it took the Gateway down.**
-- `UnitOfMeasure` now refuses a factor that is not positive and finite, which is right: a unit is a
-- dimension plus its conversion to SI, and multiplying by zero is not a conversion. What the refusal
-- did not anticipate is *where* an old row is read — `PostgresConfigurationStore.GetTagsAsync` runs
-- while the Gateway is still starting, so a single bad row stopped the whole system with a stack
-- trace rather than "failing loudly when it is read".
--
-- Three demo tags held `unit_factor_to_si = 0`, written before anything refused it: the seeding
-- script sent the field under a name the API does not have, so it bound to nothing and defaulted.
--
-- **The repair is to clear the unit, not to invent a factor.** Phase 6.5's upgrade renamed a
-- duplicate that was really there rather than refusing to start, and recorded the rename; this is the
-- same shape. Setting the factor to 1 would be the product deciding that `%` means "times one" and
-- `bar` means "pascals" — a conversion nobody declared, silently applied to stored history. Keeping
-- the symbol without a usable factor would keep a label implying a conversion that never existed.
-- Clearing says what is true: this tag has no unit, and somebody must say what it should be.

-- The repair itself. `unit_symbol` is the column the rest of the schema keys a unit's presence on, so
-- all four move together or none does.
WITH repaired AS (
    UPDATE tag
       SET unit_symbol = NULL,
           unit_dimension = NULL,
           unit_factor_to_si = NULL,
           unit_offset_to_si = NULL
     WHERE unit_symbol IS NOT NULL
       AND (unit_factor_to_si IS NULL
            OR unit_factor_to_si <= 0
            OR NOT (unit_factor_to_si = unit_factor_to_si)   -- NaN is the only value unequal to itself
            OR unit_factor_to_si = 'Infinity'::double precision
            OR unit_factor_to_si = '-Infinity'::double precision)
    RETURNING id, name, unit_symbol AS cleared_symbol
)
-- Recorded in the trail an Admin can read (ADR-0032), with a null actor, because the upgrade did it
-- and not a person. An operator who finds a tag without the unit it used to show must be able to
-- learn why, and "it stopped having a unit after an upgrade" is exactly the kind of change that
-- looks like a defect when it is not written down.
INSERT INTO audit_log (actor_user_id, action, entity_type, entity_id, detail)
SELECT NULL,
       'tag.unit_cleared_by_upgrade',
       'tag',
       repaired.id,
       jsonb_build_object(
           'tag', repaired.name,
           'reason', 'The stored unit had no usable conversion to SI (ADR-0005). It was cleared '
                     || 'rather than given a factor nobody declared; set the unit again on this tag.')
  FROM repaired;

-- And the constraint, so the database says the same thing the model does. A CHECK over a nullable
-- column accepts NULL -- `unit_factor_to_si IS NULL` is `unknown`, and a CHECK rejects only `false` --
-- which is exactly the three-valued trap this repository has been bitten by before, so the null case
-- is written out rather than left to the comparison.
ALTER TABLE tag
    ADD CONSTRAINT ck_tag_unit_converts
    CHECK (
        unit_symbol IS NULL
        OR (unit_factor_to_si IS NOT NULL
            AND unit_factor_to_si > 0
            AND unit_factor_to_si = unit_factor_to_si
            AND unit_factor_to_si <> 'Infinity'::double precision
            AND unit_factor_to_si <> '-Infinity'::double precision)
    );
