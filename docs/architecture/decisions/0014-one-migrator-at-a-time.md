# ADR-0014 — One migrator at a time, enforced by the database

**Status:** Accepted
**Complements:** ADR-0007 (DbUp, numbered scripts) and ADR-0012
(migrations run as a separate privileged step). Neither is superseded;
this ADR answers a question both left open.
**Date:** 2026-09-23

## Context

ADR-0007 made migrations a DbUp run over numbered scripts, and
explicitly set aside what happens if two runs meet: at the time they ran
inside a single-instance Gateway, so the overlap was hard to cause.
ADR-0012 moved them into their own privileged step, which makes an
overlap easy to cause — a restart loop, two deployments crossing, a
second `docker compose up`, a manual `compose run migrator`, or an
orchestrator starting a second replica in the cloud topology. Phase 6's
entry in the phase plan carried the question forward as open.

It was then answered by experiment rather than by reading, on throwaway
databases, with separate processes started at the same moment. The
result is deterministic and repeated:

- **Empty database, four migrators, the real scripts.** One succeeds;
  the other three fail loudly with `23505` on
  `pg_class_relname_nsp_index` while running `0001`. The journal ends up
  correct and the Gateway's schema check passes. This is safe only by
  accident: script `0001` creates tables, so the losers collide in the
  catalogue.
- **Existing database, one new script with no catalogue collision** (a
  data migration — an `INSERT` after a `pg_sleep`), three migrators.
  **All three exit 0. The script runs three times**: three rows in the
  table and three identical entries in `schemaversions`.

The second case is the one that matters, because it is what every
upgrade after the first looks like: the database exists and a data
script arrives. Nothing reports an error. The damage surfaces later and
somewhere else — ADR-0012's schema check compares the Gateway's script
list with the journal as a sequence, the duplicated entries do not
match, and the Gateway refuses to start for good, naming no failing or
unknown script. A silent triple-application of a data migration is worse
than a crash, and a Gateway that will not start with an unhelpful
message is the wrong place to learn about it.

The cause is in DbUp itself and is not configurable away: inspection of
`dbup-core` 6.1.1 and `dbup-postgresql` 7.0.1 shows no advisory lock and
no `LOCK TABLE` anywhere in its SQL, `schemaversions` has no unique
constraint on `scriptname`, and the order of work is read-the-journal
then execute — check-then-act, the textbook race.

A second, unrelated way to migrate nothing at all sits next to this
one. The migration scripts are embedded by a path whose case must match
the folder's, and a case-insensitive filesystem hides a disagreement
that a case-sensitive build would not. A migrator built that way embeds
zero scripts, finishes instantly reporting success, and ADR-0012's check
then compares empty with empty and also passes: both guarantees satisfied,
nothing migrated.

**Correction, recorded rather than quietly fixed (2026-09-23):** when
this ADR was written the claim was that such a mismatch already existed
in this repository, between `migrations/` in git and `Migrations/` on a
Windows working copy. It did not. The claim came from a directory
listing typed with the wrong case, which Windows answers under the name
it was given. The decision below is unchanged, because the failure mode
itself was then demonstrated in a Linux container: built from
`Migrations/`, the migrator embeds zero scripts and exits successfully.
The guard is worth having for what it prevents, not for a defect that
was there.

## Decision

**A migrator run holds a session-level PostgreSQL advisory lock for the
whole of its work — the DbUp upgrade and the application-role password
step — and only one run holds it at a time.**

- The lock is taken on the target database with a fixed, documented key,
  on a dedicated connection, before any script is read, and released
  when the run ends. If the process dies, the connection closes and
  PostgreSQL releases the lock on its own; no stale row has to be
  cleaned up by hand.
- **The wait is bounded.** A migrator that cannot take the lock within a
  configured timeout fails with a message that says another migrator
  holds the database — it does not wait forever and it does not proceed.
  A run that waits and then wins re-reads the journal and correctly does
  nothing when the winner has already applied everything.
- **A migrator that would embed zero scripts refuses to run, and the
  Gateway refuses to start against a build with zero scripts**, rather
  than treating "nothing to do" and "nothing to compare" as success.

The lock lives in the database, not in the orchestrator, because the
database is the only thing both topologies share. Compose's
`service_completed_successfully` orders the containers Compose starts
and says nothing about a second `up`, a manual run, a restart loop, or a
second replica.

No new dependency: Npgsql already ships with the migrator, so ADR-0006's
table is unchanged.

## Consequences

- Concurrent migrator runs become one run and one no-op, on any
  topology, including ones not yet built.
- A data migration can be written without asking whether it is
  idempotent under concurrency. It still must be correct under a retry.
- A hung migrator is visible as a timeout in the run that wanted the
  lock, naming the cause, instead of as a duplicated migration whose
  consequence appears a deploy later.
- The migrator now fails in one more way — lock-wait timeout — which is
  deliberate: refusing to start is the cheap failure; applying a data
  script twice is not.
- Migrations remain forward-only (ADR-0007). This ADR does not introduce
  down-scripts; the rollback procedure ADR-0012 left to Phase 6 is a
  documented backup-and-restore step in the deployment guide, taken
  before the migrator runs.

## Verified in review by

- The migrator takes a session-level advisory lock on a dedicated
  connection before reading any script, and the lock covers the password
  step as well as the DbUp upgrade.
- There is a test in which several migrators run **against an existing
  database** with a new data script, and it asserts the script applied
  **once**: one row, one journal entry. Removing the lock makes that
  test fail with the triple application — the mutation is part of the
  evidence, not an argument.
- There is a test that a migrator which cannot take the lock fails
  within the configured timeout, with a message naming the reason.
- Migrations against *different* databases do not block each other —
  the parallel test suites depend on this.
- A build that embeds no migration scripts fails a test, rather than
  producing a migrator that succeeds instantly.
