-- The devices an edge says it has been assigned and cannot read (ADR-0021).
--
-- ADR-0019 §8 refuses a device at the save that assigns it to an edge which has declared it lacks
-- the driver. That covers the assignment made after the declaration; it does not cover an edge
-- redeployed without a driver it used to have, because no save happens then. The device stays
-- assigned, nothing reads it, its tags go Bad by the staleness rule (ADR-0016), and nothing says
-- why — the same shape of defect §8 closed, with the cloud silent instead of the plant.
--
-- jsonb rather than text[]: unlike driver_keys, an entry is a pair (a device name and the driver it
-- needs), so it is not a list of scalars. The checks the payload makes — a missing name, a missing
-- driver, the same device twice — stay the payload's, as they do for driver_keys: a column cannot
-- refuse a list an edge stated, only record it as stated.
--
-- Nullable, and null is deliberately not empty, for the reason driver_keys is. Null is "no
-- declaration has said anything about this" — an edge that has never connected, or one whose build
-- writes version 1 of the payload, which carries no such field (ADR-0021 §2). Empty is "this edge
-- has said it can read everything assigned to it" — a statement, and a different one.
ALTER TABLE edge ADD COLUMN unreadable_devices jsonb;

-- edge_active names its columns, so adding one means recreating it — the maintenance cost
-- migration 0004 predicted, and 0007, 0012, 0013 and 0014 have each paid. Nothing is built on
-- edge_active but the repository.
DROP VIEW edge_active;

CREATE VIEW edge_active AS
SELECT id, tenant_id, name, link_device_id, driver_keys, drivers_declared_at, unreadable_devices
FROM edge
WHERE deleted_at IS NULL;
