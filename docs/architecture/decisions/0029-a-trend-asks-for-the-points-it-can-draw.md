# ADR-0029 — A trend asks for the points it can draw, and the reduction happens in the query

**Status:** Accepted
**Date:** 2026-10-07

## Context

**Every reading in the window crosses the wire.** Measured on 2026-10-07 while building the trend
window picker (this repository's PR #6): `GET /api/tags/{id}/history` for seven days of one tag
scanned once a second came back as **100,552 samples and 22.2 MB**, in 0.43 s over localhost.

The picker is what made it reachable. Before it the client asked for fifteen minutes, hard-coded in
two places, so the largest answer this endpoint could be asked for was about nine hundred samples.
The client already reduced those for drawing (`trendColumns`, min/max per pixel), which is what made
a long window a curve rather than a block of ink — but it reduced them *after* they had all crossed
the wire and been materialised by the Gateway to be reduced.

**There are two hops, and in a deployment both of them are wires.** The database is its own container
(`deploy/docker-compose.yml`), so the rows cross one wire to reach the Gateway even when the browser
is on the same machine; the browser may be on a plant LAN or, in the cloud topology, at the far end of
the link ADR-0017 describes. Half a second of localhost is not a number to carry to that second hop,
and a plant that has been running for months holds several times the window that was measured.

**Why this needs a decision rather than a patch.** "Reduce it in the query" sounds like a TimescaleDB
feature, and ADR-0006 carries a standing flag: continuous aggregates and native compression are
licensed under the Timescale License rather than Apache 2.0, and are not to be relied on until a legal
review says otherwise. So *where the reduction happens* is a question about which features this
project may lean on, and it is not one to settle inside an implementation pull request.

**And the answer must not be a cap.** `open-work.md` §3 recorded this while the measurement was fresh,
and it is the one answer this project must not take: a cap that dropped readings without saying so
would leave a reader looking at a curve that is thinner than the record with nothing on it to say so
— the failure ADR-0003 exists to prevent, reached from the other side.

## Decision

**1. The reduction happens in the query — not in the browser, and not in the Gateway.**

Three placements were available and the query is the only one that removes the traffic rather than
moving it:

- **In the browser** is what happens today, and it is the measurement above: correct, and too late.
- **In the Gateway**, after reading every row, removes the second hop and leaves the first. It also
  means the Gateway materialises a hundred thousand rows per open trend in order to throw almost all
  of them away — a cost that grows with the window exactly as the traffic does.
- **In the query** reads the readings where they are indexed and returns the points. The rows that
  are not drawn are never built into objects, never serialised, and never sent.

**2. It is `date_bin`, which is PostgreSQL's own function — so this leans on no TimescaleDB feature
and ADR-0006's flag is untouched.**

`date_bin(stride, source, origin)` has been in PostgreSQL since 14 and does exactly this: fixed-width
bins, aligned to an origin the caller names. The database is PostgreSQL 17
(`timescale/timescaledb:2.17.2-pg17`), so it is there. `time_bucket` would also have been available in
the Apache build, and was rejected for being a *second* thing under ADR-0006's licensing question when
the standard function answers the same question — the flag stays about compression and continuous
aggregates, and about nothing else. A future change that wants `time_bucket`'s calendar-aware offsets
is a new decision, not an extension of this one.

**3. The caller asks for `points` — how many points it can draw — and the server chooses the width and
says which one it chose.**

- `GET /api/tags/{tagId}/history?from=…&to=…&points=600`.
- **The width is `ceil(span / points)` milliseconds**, so the answer holds **at most** `points`
  buckets and exactly `points` whenever the span divides. The shortest window with the chart's 600
  points gives a 1500 ms bucket, which is the same grouping the browser used to do for itself — so
  nothing an operator sees today becomes coarser. A seven-day window gives 1008 s.
- **No `points` means a raw read, exactly as before.** Reduction is requested, never applied silently:
  a caller that wants every reading is not denied one, and a response that is complete can never be
  mistaken for one that is not. Reporting will want the raw path back.
- **`points` is refused outside 2..2000, by name, rather than clamped**, for the reason §2.0f refused
  an out-of-range `responseTimeoutSeconds`: a caller whose request was quietly coarsened gets a
  picture it did not ask for, and nothing anywhere says so. Two is the fewest points that can be a
  line; 2000 is more detail than any screen this project draws and marks where "reduce this" has
  stopped being a reduction.
- **The answer states what it did.** `bucketMilliseconds` is non-null exactly when the answer is a
  reduction, and `samples` is populated exactly when it is a reading — **one or the other, never a
  mixture**, because a mixture is a payload whose completeness a reader has to work out.
- **Why not a fixed list of resolutions** (1 s, 1 min, 1 h), which was the other candidate: it makes
  the caller answer a question about the server's storage, and the question it is really asking is
  *how much room do I have* — the one number it knows and the server does not. It would also let a
  caller ask for a resolution finer than it can draw, which is the traffic this decision is removing.

**4. A bucket carries the extremes of the readings the raw path would have plotted, the count of
everything read in it, and the time of the last reading in it.**

- **`low`/`high` are the min and max of the bucket's Good numeric readings** — precisely the set the
  chart would have drawn from a raw array. This is the rule that keeps a reduced picture from
  disagreeing with a raw one: **a reduced read must never show a value a raw read of the same window
  would have dropped.** A Bad sample that carried a stale number, a boolean's absent numeric, an
  Uncertain reading: none of them may widen an envelope.
- **Never an average and never every n-th reading**, both for the reason the client arrived at by
  being wrong first on 2026-10-07: an average flattens the spike that made somebody open the trend,
  and decimation throws it away — the one-second excursion that tripped an alarm is exactly the
  sample a thinning pass discards. The reduction moves; the rule does not.
- **`count` is how many readings were read in the bucket, whatever their quality**, which is why a
  bucket whose readings were none of them plottable is **returned** rather than dropped: `count` above
  zero with no envelope. A device answering with nothing but Bad is then a hole on the chart rather
  than a silent bucket, and `sum(count)` is the number of readings in the window — the number the
  caption has always shown.
- **`lastUtc` is the newest reading in the bucket.** Freshness is ADR-0003's other half — a trend that
  has stopped growing has to say so — and with buckets it is a fact about the newest `lastUtc` in the
  window. **`startUtc` cannot answer it, because it is not a measurement time at all**: it is the edge
  of the stretch, derived from the request, and no reading need have happened there. Deciding age on
  it would put a time nothing was measured at into the sentence *no reading since …*, and would call a
  trend stale up to a bucket early — seventeen minutes early on a seven-day window.
- **An empty bucket is absent, not zero.** `GROUP BY` produces no row for a stretch nothing was
  measured in, and nothing is invented for it. A missing bucket is how a gap is drawn, which is the
  same rule as a Bad reading carrying no value rather than a substituted one.

*Corrected 2026-10-07, in place, before anything was implemented against this ADR: the reason given
above for `lastUtc` was first written the other way round — that a chart deciding age on `startUtc`
would report a dead instrument as up to a bucket **fresh**. That is backwards, and the test written
for it refused to fail: `startUtc` is always at or before `lastUtc`, so an age measured from it runs
ahead of the truth rather than behind it, and the failure is a live trend reported stale early rather
than a dead one reported alive. The reason `lastUtc` is in the payload is the one now given — a bucket
edge is not a measurement time, and ADR-0003 does not let a time nothing was measured at stand in for
the last reading's.*

**5. The grid is anchored to the window the caller asked for.**

`date_bin(width, source_time, from)` — the origin is the request's own `from`. The first bucket
therefore starts exactly at the window's edge, the last ends at or before `to`, and **the same request
returns the same grid**. Anchoring to the epoch or to midnight would leave the first and last buckets
partial, and would make a bucket boundary a fact about the calendar rather than about the question
that was asked.

**6. The client draws one column per bucket, and the gap rule becomes exact.**

- **x is the midpoint of the bucket's span.** The readings inside it are within one bucket width of
  that position by definition, which is the honest thing to draw once the individual times are no
  longer in the payload.
- **A missing bucket is a break in the line**, found by comparing bucket ordinals — the client knows
  the width and the window, so `ordinalNext − ordinalPrev > 1` is exact at every resolution. This
  replaces a heuristic that could not be exact: the chart inferred an outage by comparing the distance
  between columns against four times their median, which cannot see a hole narrower than about four
  columns — and at a 1008 s bucket, four columns is over an hour of nothing. A gap in a seven-day
  trend is drawn as a gap.
- **The chart states the reduction**: *N readings · M points*, which is the caption it already had
  plus the fact that a reduction happened.
- **A payload with no `bucketMilliseconds` is not something this client draws**, and it draws nothing
  rather than falling back to reducing raw samples in the browser — a fallback nothing would ever
  exercise, and a second place for the same rule to live. The Gateway serves the client from its own
  origin as one build (Phase 6, ADR-0028), so a client asking a server that cannot reduce is not a
  deployment state this project supports.

## Consequences

- **The answer stops growing with the window.** A seven-day trend is at most `points` rows of five
  numbers — smaller, in bytes, than the quarter-hour answer that was the only one available before
  the picker existed. The 22.2 MB response is gone, and what replaces it does not depend on how long
  the plant has been running.

- **The envelope rule moves into SQL, and so do its tests.** "Min and max per bucket, and no invented
  value" is now a claim about a query, verified against TimescaleDB rather than against a pure
  function in the browser. The client's `trendColumns` and its tests are **deleted rather than kept as
  a fallback**: what they pinned does not disappear, it moves — the extremes rule to the query's own
  tests, the gap rule to the client's new one.

- **`IHistorian` gains a bucketed read, and it takes a width rather than a point count.** `points` is
  an HTTP question about a screen; a bucket width is a storage question. A storage interface that knew
  how many pixels a caller had would be the wrong shape for the boundary ADR-0002 draws.

- **Nothing about the existing contract breaks.** Without `points` the endpoint answers exactly as it
  did, so the raw path, the tests that use it and any future export keep working.

- **Still open, deliberately.** A **non-numeric** history: `low`/`high` are numbers, so a text or
  discrete tag has nothing here to reduce, and a state history would want first-and-last per bucket —
  a different question with a different answer. And **precomputation is untouched**: this reduces a
  query and computes nothing in advance, so continuous aggregates and compression remain exactly as
  ADR-0006 left them, and the legal review is still the gate on both.

## Verified in review by

- A request with `points` returns at most that many buckets, states `bucketMilliseconds`, and holds no
  `samples`; the same request without `points` returns the readings and no buckets.
- A bucket's `low`/`high` are the min and max of the Good numeric readings in it: a Bad outlier wider
  than every Good reading does not widen the envelope, and a Good spike does not disappear from it.
- A stretch nothing was measured in produces no bucket, `sum(count)` over the buckets equals the
  readings in the window, and a bucket of nothing but Bad readings comes back with a count and no
  envelope.
- A trend with a hole in it draws a broken line at that hole, at a window where the old heuristic
  could not have seen it.
- A trend that has stopped growing says *no reading since* a time that a reading was measured at, and
  is not declared stale a whole bucket before it is.
- `points` outside 2..2000 is refused with the value and the range in the message.
