-- tag_active must not hand out a tag whose device has been deleted (ADR-0009).
--
-- Deleting a device cascades to its tags in one transaction, so a crash between the two
-- statements leaves nothing committed. What the transaction cannot cover is a tag
-- inserted by another transaction that commits after the cascade's UPDATE has run: at
-- READ COMMITTED that row is never seen, and it survives as a live tag belonging to a
-- device that no longer exists. The browse tree would then show nothing while the live
-- tag list still reported a value — exactly the quiet inconsistency this ADR exists to
-- prevent.
--
-- Making the view carry the condition removes the ordering question entirely: the
-- invariant holds however the rows got into that state.
--
-- The column list stays explicit, and stays the tag's own columns only, so callers see
-- the same shape as before.
DROP VIEW tag_active;

CREATE VIEW tag_active AS
SELECT t.id, t.device_id, t.name, t.value_kind, t.unit_symbol, t.unit_dimension,
       t.unit_factor_to_si, t.unit_offset_to_si, t.source_address, t.is_writable
FROM tag t
JOIN device d ON d.id = t.device_id
WHERE t.deleted_at IS NULL
  AND d.deleted_at IS NULL;
