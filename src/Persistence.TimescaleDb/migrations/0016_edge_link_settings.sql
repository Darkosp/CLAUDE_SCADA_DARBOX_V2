-- The two settings of an edge's link that are not constants (ADR-0022).
--
-- An edge's link device is derived by the Gateway and is not overridable. Its host, port, TLS flag
-- and three certificate paths come from the deployment's own EdgeProvisioningOptions, and its topic
-- is the edge's own name — none of those can vary, so none of them is stored, because a column for
-- a setting that cannot vary is a column an operator could make wrong.
--
-- What can vary is how long silence may last before the edge's tags read Bad (ADR-0016, ADR-0019
-- §5), and how long the broker queues for a Gateway that is away. Both are properties of the link,
-- and the link is the edge, so both live here. The defaults are exactly the values the MQTT driver
-- already used for a hand-made link device, so an edge that says nothing behaves as it does today.
ALTER TABLE edge ADD COLUMN link_staleness_seconds integer NOT NULL DEFAULT 60;

-- 720 hours is 30 days: the default the MQTT pushing driver carries (DefaultSessionExpiry).
ALTER TABLE edge ADD COLUMN link_session_expiry_hours integer NOT NULL DEFAULT 720;

-- Not null with a default, rather than nullable: unlike driver_keys and unreadable_devices, there is
-- no "nobody has told us" state here. Every edge has a link, and a link always has a limit — the
-- default is the limit until an operator says otherwise. A null would be a third state meaning
-- nothing.
--
-- The checks the driver makes — a positive number, at most ten years — stay the driver's and the
-- API's: a column cannot refuse a value an operator typed, only record it as typed.
ALTER TABLE edge
    ADD CONSTRAINT edge_link_staleness_seconds_positive CHECK (link_staleness_seconds > 0),
    ADD CONSTRAINT edge_link_session_expiry_positive CHECK (link_session_expiry_hours > 0);

-- edge_active names its columns, so adding any means recreating it — the maintenance cost
-- migration 0004 predicted, and 0007, 0012, 0013, 0014 and 0015 have each paid. Nothing is built on
-- edge_active but the repository.
DROP VIEW edge_active;

CREATE VIEW edge_active AS
SELECT id, tenant_id, name, link_device_id, driver_keys, drivers_declared_at, unreadable_devices,
       link_staleness_seconds, link_session_expiry_hours
FROM edge
WHERE deleted_at IS NULL;
