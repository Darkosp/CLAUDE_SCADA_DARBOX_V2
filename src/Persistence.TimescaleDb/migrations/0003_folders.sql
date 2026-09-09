-- Free-form nested folders below Site (ADR-0001 §4). A folder is an
-- organisational node only: it carries no driver, alarm or security behaviour
-- of its own (ADR-0001 §6).
CREATE TABLE folder (
    id               uuid PRIMARY KEY,
    site_id          uuid NOT NULL REFERENCES site (id),
    parent_folder_id uuid,
    name             text NOT NULL
);

-- The composite key that lets a folder reference carry its site along with it.
ALTER TABLE folder
    ADD CONSTRAINT uq_folder_site_id UNIQUE (site_id, id);

-- A folder's parent must belong to the same site. Enforced by the key rather
-- than by repository code: cross-site placement is a security boundary, and a
-- check that lives only in application code is bypassable by any future path
-- that writes this table.
--
-- The default MATCH SIMPLE semantics are what make a root folder legal: when
-- parent_folder_id is NULL the constraint is not checked at all.
ALTER TABLE folder
    ADD CONSTRAINT fk_folder_parent_same_site
    FOREIGN KEY (site_id, parent_folder_id) REFERENCES folder (site_id, id);

-- A device may sit in a folder, or directly under its site (NULL) as every
-- device did before this migration.
ALTER TABLE device
    ADD COLUMN folder_id uuid;

-- Same rule one level down: a device's folder must belong to the device's own
-- site. device.site_id remains the real tenant/security scope (ADR-0004);
-- folder_id is organisational only.
ALTER TABLE device
    ADD CONSTRAINT fk_device_folder_same_site
    FOREIGN KEY (site_id, folder_id) REFERENCES folder (site_id, id);

CREATE INDEX ix_folder_site_parent ON folder (site_id, parent_folder_id);
CREATE INDEX ix_device_folder ON device (folder_id);
