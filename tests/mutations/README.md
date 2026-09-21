# Mutation patches

Each patch breaks one guarantee on purpose, so the test that claims to protect
it can be seen failing. A test that has only ever passed has not been shown to
test anything — a filter test especially, because it can pass for the wrong
reason.

Run each from the repository root:

```
git apply tests/mutations/<patch>
dotnet test --filter <NamedTest>
git checkout .
```

Do **not** pass `--no-build`. A stale test assembly reports the previous run's
result and the mutation looks like it changed nothing — this has already
happened once in this repository.

A database must be up (`docker compose up -d`), or these tests skip, and a
skipped test proves nothing.

## `m1-naive-site-filter.patch`

Drops `OR (e.site_id IS NULL AND e.occurrence_id IS NULL)` from the journal's
Site filter, leaving the naive `site_id = ANY(@sites)`. Since a null never
equals anything, every engine-level event — `EvaluationStarted`,
`EvaluationStopped`, `JournalGap` — disappears for any non-Admin reader, and an
evaluation outage reads as a quiet period. ADR-0013 exists to prevent exactly
that.

Must fail:

```
dotnet test --filter The_journal_shows_every_reader_the_engine_events_that_belong_to_no_site
```

This is the mutation that answers "did the test pass because the filter is
right, or because no row without a Site was there to drop?". The test asserts
those rows are **present**, so an empty journal fails it too — it cannot pass
vacuously.

## `m2-no-site-condition.patch`

Neutralises the Site condition altogether (`@sites IS NULL OR TRUE`), keeping
the parameter referenced so the query still binds and the failure is the
filter's, not a stray-parameter error. Every reader then sees every Site.

Must fail:

```
dotnet test --filter The_journal_hides_events_belonging_to_a_site_the_reader_cannot_see
```

The two together pin both directions: rows belonging to no Site must survive
the filter, and rows belonging to someone else's Site must not.

## After either

`git checkout .` restores the source. Re-run the full suite and confirm it is
green again before drawing any conclusion — a mutation left applied silently
poisons every later run.
