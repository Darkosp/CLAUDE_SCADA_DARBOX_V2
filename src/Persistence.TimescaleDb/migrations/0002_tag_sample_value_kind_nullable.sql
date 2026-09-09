-- A reading whose quality is Bad carries no value at all — not a zero, not a
-- false, not an empty string (ADR-0003). Such a sample therefore has no kind
-- and no typed column set, so the discriminator must be nullable.
ALTER TABLE tag_sample ALTER COLUMN value_kind DROP NOT NULL;
