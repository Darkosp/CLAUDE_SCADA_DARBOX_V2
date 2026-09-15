# ADR-0012 — Migrations run outside the Gateway process

**Status:** Accepted
**Supersedes:** ADR-0007's migration-execution clause only — the rest of
ADR-0007 (DbUp, numbered scripts, embedded as resources, never a manual
`ALTER`) stands unchanged.
**Date:** 2026-09-11

## Context

ADR-0007 decided that migrations run automatically as a startup step
inside the Gateway host, before it serves anything. That was the right
call at the time: it makes it impossible to deploy an application
without the schema it depends on, which is the failure mode that
actually bites during early development.

ADR-0011 then introduced a database role split. The application connects
as a non-superuser role with only `INSERT`/`SELECT` on `audit_log`, so
that an append-only audit trail is enforced by the database rather than
by convention. But migrations need a privileged role — they create
tables, views and grants — and under ADR-0007 the Gateway process is
what runs them. That means the process serving HTTP requests would have
to hold the credential that bypasses the very guarantee ADR-0011 exists
to provide. The audit trail would then be protected against application
bugs, but not against a compromised or misbehaving Gateway, while the
ADR claims the stronger property. A review criterion could pass while
the process holding the key sits one layer away from the thing the key
unlocks.

The naive fix — just move migrations to a separate step — gives up what
ADR-0007 was protecting: nothing would stop a Gateway from starting
against a database that had never been migrated, or had been migrated to
the wrong version.

## Decision

Migrations run as a **separate, one-shot step before the Gateway
starts** — a migrator CLI/container, using the privileged role. The
Gateway never holds or reads the privileged credential.

The Gateway, at startup, **reads DbUp's journal table over its own
non-privileged connection and refuses to start unless the journal
matches exactly the set of scripts that build carries** — not merely
"is not behind" — rather than trying to fix it. A Gateway older than
its database is refused for the same reason as one newer than it: it was
built against a schema that no longer exists, and the mismatches that
matter are rarely the ones that fail loudly. ADR-0009's migration 0005,
which changed what `tag_active` returns without changing its column
list, is the shape of the problem: older code would keep querying it and
quietly see different rows. The
safety property ADR-0007 actually cared about — never serve requests
against a schema in an unknown or stale state — is preserved and is now
checked explicitly, instead of being a side effect of the Gateway also
being the thing that migrates. What changes is only *who applies* the
change, not whether the application can run ahead of its schema.

Everything else in ADR-0007 is untouched: DbUp, numbered `.sql` scripts
in order, embedded as assembly resources so a deployment can never be
separated from its migrations, journal-tracked so re-running is a no-op,
and never a hand-applied `ALTER` against a running database.

Phase 6's deployment packaging carries this: both topologies run the
migrator to completion before the Gateway container starts, and the
privileged connection string exists only in the migrator's environment.

## Consequences

The serving process can no longer escalate its own database privileges,
which is what makes ADR-0011's append-only `audit_log` mean what it
says. The cost is an extra deployment step and a second connection
string, plus a startup failure mode that is new: a Gateway that refuses
to start because someone forgot to run the migrator. That failure is
loud, early, and says exactly what is wrong — which is the right trade
against a Gateway that starts happily against a schema it doesn't match,
or one that holds a credential it should never have needed.

Local development gains the same step. A developer who pulls a branch
with a new migration and starts the Gateway gets a clear refusal naming
the expected and actual versions, instead of a runtime error somewhere
further in.

**Exact matching has a real cost, stated plainly: rolling the Gateway
back after a migration has run does not work.** DbUp has no down
scripts, so the previous build will refuse to start against the migrated
database. The intended recovery from a bad release is therefore to roll
*forward* — fix and deploy again — not to roll back. That is a
deliberate trade: for a historian, restoring a database backup to enable
a rollback throws away every sample written since the backup, which is
worse than the outage it is meant to shorten. If a deployment ever needs
a genuine rollback path, it needs down-migrations or a documented
restore procedure, and that is a Phase 6 deployment decision rather than
something to improvise during an incident.

## Verified in review by

- The Gateway's configuration contains no privileged connection string,
  and nothing in the Gateway calls DbUp's upgrade path.
- Starting the Gateway against a database whose journal does not match
  the build's scripts exactly fails at startup, naming both versions,
  and serves no requests — whether the journal is behind the build *or
  ahead of it*.
- Starting the Gateway against a correctly migrated database succeeds
  using only the non-privileged application role.
- The migrator applies cleanly from a completely empty database and is a
  no-op on a second run (ADR-0007's own criterion, unchanged).
- Docker Compose for both topologies (Phase 6) runs the migrator to
  completion before the Gateway starts.
