# 0007 — Pagination in query params and response headers

**Status:** Accepted

## Context

Every list endpoint could, in principle, return an unbounded number of rows.
That is a latent production hazard: a workspace with tens of thousands of
conversations turns an innocent `GET` into a memory and bandwidth problem.
Pagination has to be added — but the API already has clients and a test suite
that expect list bodies to be plain JSON arrays, and wrapping every response in
a `{ data, total, page }` envelope would break all of them at once.

## Decision

Put the page selection in the query string and the totals in response headers,
leaving bodies as plain arrays.

- Request: `?page=` (1-based) and `?pageSize=` (default `Paging.DefaultPageSize`
  = 50, max `Paging.MaxPageSize` = 200).
- Response headers: `X-Total-Count`, `X-Page`, `X-Page-Size`, `X-Total-Pages`.
- Omitting the parameters selects the **first page**, not everything — so the
  bound is enforced even for a caller that says nothing.
- Out-of-range input is **clamped**, not rejected: a page past the end is an
  empty page, which is what a client walking pages expects; a `pageSize` over the
  max is capped.

## Consequences

- No list endpoint can return an unbounded result set, which was the point —
  while existing clients and tests keep working because the body shape did not
  change.
- Total counts and page metadata are available without parsing the body.
- The binding parameter is named `paging`, not `page`: a `[FromQuery]
  PageRequest page` collided with the object's own `Page` property and silently
  ignored `?page=`, and every test passed because they all asserted page one.
  The rename fixed it; `PaginationBoundaryTests` walks to a real second page on
  every list endpoint to guard the regression.
- Clamping instead of rejecting means a bad `page`/`pageSize` never surfaces as a
  client error — a deliberate trade for robustness over strictness. Truly
  unparseable values (`?page=abc`) still `400` via model binding.
