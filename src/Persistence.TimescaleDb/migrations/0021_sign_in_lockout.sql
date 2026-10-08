-- Failed sign-ins are counted against the account and lock it for a window (ADR-0031).

ALTER TABLE app_user
    ADD COLUMN failed_sign_ins integer NOT NULL DEFAULT 0,
    ADD COLUMN locked_until    timestamptz;

-- The count cannot go negative, and it is checked rather than trusted because two writers touch this
-- column: the sign-in path increments it and an Admin's password reset clears it. A negative count would
-- make the threshold unreachable, which is a lockout that silently is not one.
ALTER TABLE app_user
    ADD CONSTRAINT ck_app_user_failed_sign_ins CHECK (failed_sign_ins >= 0);

-- app_user_active names its columns, so the new ones are invisible through it until it is rebuilt. Nothing
-- else in the schema is built on this view (checked: migration 0008 creates it and no later migration
-- references it), so the cascade is one view rather than the two that tag_active drags along.
DROP VIEW app_user_active;

CREATE VIEW app_user_active AS
SELECT id, tenant_id, username, password_hash, is_admin, created_at, failed_sign_ins, locked_until
FROM app_user
WHERE deleted_at IS NULL;

-- Re-granted explicitly, for the reason migration 0017 records and 0020 repeats: a view dropped and
-- recreated is a new object, and granting on the table underneath does not grant on the view over it.
-- Without this every sign-in fails with a permission error whose cause is three files away.
GRANT SELECT ON app_user_active TO scada_app;
