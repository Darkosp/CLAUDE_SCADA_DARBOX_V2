-- The driver keys an edge says it has (ADR-0019 §8).
--
-- The edge declares them, because which driver keys exist is a fact about the build running at the
-- plant. The cloud's own list is not that list: the Gateway registers Modbus, OPC UA and MQTT
-- while an edge registers Modbus and OPC UA, so the cloud holds a key no edge can read with, and a
-- build that gave an edge a driver the cloud lacks would be the same mistake mirrored. Without
-- this column a device naming a driver its edge does not have is accepted, derived into the
-- configuration, published, and refused only by the edge — loud at the plant, silent in the cloud.
--
-- text[] rather than jsonb: the value is a list of driver keys and nothing else, and the schema
-- should say so. The checks the payload makes — a key that is not text, a blank one, one key twice
-- under two spellings — stay the payload's; a column cannot refuse a list an edge stated, only
-- record it as stated.
--
-- Nullable, and null is deliberately not empty. Null is "this edge has never declared": the
-- ordinary state of an edge whose plant is still being configured, and the reason an assignment to
-- it is accepted rather than refused. Empty is "this edge said it has none" — a statement, and a
-- different one.
ALTER TABLE edge ADD COLUMN driver_keys text[];

-- When the cloud read that declaration. The cloud's own clock (ADR-0017): an edge's clock is
-- neither trusted nor overwritten, so this is never filled from anything the edge sent.
ALTER TABLE edge ADD COLUMN drivers_declared_at timestamptz;

-- edge_active names its columns, so adding one means recreating it — the maintenance cost
-- migration 0004 predicted, and 0007, 0012 and 0013 have each paid. Nothing is built on
-- edge_active but the repository.
DROP VIEW edge_active;

CREATE VIEW edge_active AS
SELECT id, tenant_id, name, link_device_id, driver_keys, drivers_declared_at
FROM edge
WHERE deleted_at IS NULL;
