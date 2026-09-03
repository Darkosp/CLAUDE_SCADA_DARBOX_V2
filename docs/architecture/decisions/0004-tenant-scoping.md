# ADR-0004 — Tenant scoping from day one

**Status:** Accepted
**Date:** 2026-09-03

## Context

The platform is deployed single-tenant-per-instance (each customer gets
their own isolated deployment, cloud-hosted or on-premises — see the
deployment topology discussion; a dedicated ADR for that follows separately).
Even so, retrofitting a tenant/organization-scoping column onto a historian
table that already holds years of real data is exactly the class of
expensive, risky migration this project is trying to avoid by design — the
same reasoning already applied to Site scoping in ADR-0001. A column that is
always a single constant value costs essentially nothing to include now.

## Decision

The data model includes an explicit **Tenant** entity above Site:
`Tenant → Site → Device → Tag`. Every deployed instance contains exactly one
Tenant row (the customer that instance serves), enforced by deployment
convention rather than by removing the column. Tenant does not appear in the
human-visible hierarchical tag path (unlike Site) — within a single-tenant
instance it is always the same value and would only add noise.

This decision does not commit the platform to ever offering a shared,
multi-tenant-per-instance deployment; it only keeps that door open at
near-zero cost, rather than closing it in a way that would later require a
historian migration to reopen.

## Consequences

Every core table that is Site-scoped is also, transitively, Tenant-scoped,
at no meaningful extra cost today. If a future business need for shared
multi-tenant hosting ever arises, the schema already supports it; if it
never arises, the cost paid today is negligible (one constant-valued column
family).

## Verified in review by

- Any new core entity that is Site-scoped also carries (directly or via
  Site) a Tenant association.
- TenantId never appears in the human-visible tag path.
