-- Users, Site roles, sessions and the audit trail (ADR-0011), plus the separate
-- application database role that ADR-0011 and ADR-0012 require.
--
-- Table names avoid the SQL keywords user and role (ADR-0011), and so does the one
-- column that would otherwise have been called role.

CREATE TABLE app_user (
    id            uuid PRIMARY KEY,
    tenant_id     uuid NOT NULL REFERENCES tenant (id),
    username      text NOT NULL,
    -- PBKDF2 via PasswordHasher (ADR-0011). Never the password, never reversible.
    password_hash text NOT NULL,
    -- Admin is tenant-wide and a flag on the user, not a row in user_site_role.
    is_admin      boolean NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    deleted_at    timestamptz
);

-- A username is unique among live users only, so a deactivated account's name can be
-- given to someone new. Nothing references a user by name: the audit trail and any
-- acknowledgement carry the user's id.
CREATE UNIQUE INDEX ux_app_user_username_active
    ON app_user (lower(username))
    WHERE deleted_at IS NULL;

-- Soft delete through a view, as for every other configuration entity (ADR-0009). A
-- deactivated user disappears from authentication and authorisation, while their id
-- still resolves in the audit trail.
CREATE VIEW app_user_active AS
SELECT id, tenant_id, username, password_hash, is_admin, created_at
FROM app_user
WHERE deleted_at IS NULL;

CREATE TABLE user_site_role (
    user_id   uuid NOT NULL REFERENCES app_user (id),
    site_id   uuid NOT NULL REFERENCES site (id),
    -- The CHECK is what keeps Admin out of this table: Admin is tenant-wide, so a row
    -- claiming it for one Site would describe a permission the model does not have.
    site_role text NOT NULL CHECK (site_role IN ('Viewer', 'Operator')),
    PRIMARY KEY (user_id, site_id)
);

-- An opaque session token (ADR-0011). Only its SHA-256 hash is stored, so a copy of
-- this table cannot be replayed as a set of live logins.
CREATE TABLE session (
    id           uuid PRIMARY KEY,
    user_id      uuid NOT NULL REFERENCES app_user (id),
    token_hash   bytea NOT NULL UNIQUE,
    -- Both clocks live on the row: absolute lifetime runs from created_at, idle
    -- timeout from last_seen_at (ADR-0011).
    created_at   timestamptz NOT NULL,
    last_seen_at timestamptz NOT NULL,
    revoked_at   timestamptz
);

CREATE INDEX ix_session_user ON session (user_id);

-- Deliberately no foreign key to app_user: the trail must record events about accounts
-- regardless of what later happens to them, and a failed login may name no account at
-- all. Same reasoning as tag_sample having no foreign key to tag.
CREATE TABLE audit_log (
    id            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    occurred_at   timestamptz NOT NULL DEFAULT now(),
    actor_user_id uuid,
    action        text NOT NULL,
    entity_type   text,
    entity_id     uuid,
    detail        jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX ix_audit_log_occurred ON audit_log (occurred_at);

-- The application's own database role (ADR-0011, ADR-0012). It is created without a
-- password and without LOGIN: a migration script is committed and reviewable by design,
-- so it is the wrong place for a credential. The migrator sets the password from an
-- environment variable after the scripts have run.
--
-- Roles are cluster-wide rather than per database, so two databases migrating at the
-- same time (parallel test fixtures) can both find it missing; the second one's CREATE
-- then fails as a duplicate, which is the outcome both wanted. A race that close can
-- surface as the catalogue's unique index rather than as duplicate_object.
DO $$
BEGIN
    CREATE ROLE scada_app NOLOGIN;
EXCEPTION
    WHEN duplicate_object OR unique_violation THEN NULL;
END
$$;

GRANT USAGE ON SCHEMA public TO scada_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO scada_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO scada_app;

-- The historian's hypertable is granted by name as well: TimescaleDB propagates a grant
-- on a hypertable to its chunks, and naming it keeps that from depending on whether the
-- schema-wide form above is propagated the same way.
GRANT SELECT, INSERT ON tag_sample TO scada_app;

-- Tables created by later migrations are reachable without each script remembering to
-- grant them — the same "the wrong thing should not depend on someone remembering"
-- reasoning as the active views.
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO scada_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO scada_app;

-- Append-only, enforced by the database rather than by convention (ADR-0011). This
-- REVOKE has to come after every broader grant above, or one of them would silently
-- hand the rights back.
REVOKE UPDATE, DELETE, TRUNCATE ON audit_log FROM scada_app;

-- The Gateway reads the journal to check the schema version at startup (ADR-0012), but
-- must never be able to rewrite it — a Gateway that could mark scripts as applied could
-- talk its own version check into passing.
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON schemaversions FROM scada_app;
