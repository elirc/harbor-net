# 0005 — Compile segment rules to SQL

**Status:** Accepted

## Context

A segment is a dynamic group of contacts defined by rules over their fields and
custom attributes — "enterprise plan", "seen since January", "email ends with
@acme.test". Membership has to be *live*: the moment a contact's attributes
change, they should join or leave, with nothing to refresh. And it has to scale:
a workspace can have very many contacts, and a segment feeds both a membership
listing and a conversation filter (`?segmentId=`).

The tempting shortcut is to load the workspace's contacts and filter them in
memory with a compiled predicate. That works on ten contacts and falls over on a
million — and worse, the conversation filter would have to materialize a list of
matching contact ids and pass it back into a second query.

## Decision

Compile the rules to SQL and keep membership a query.

- `SegmentCompiler.Compile` builds an `Expression<Func<Contact, bool>>` that EF
  Core translates to a `WHERE` clause. Built-in fields map to columns; custom
  attributes (`attributes.<key>`) are read with SQLite's `json_extract`, exposed
  to EF as a `[DbFunction]` on `HarborDbContext`.
- The conversation filter composes the segment as a **subquery over contact
  ids** (`SegmentsController.ContactIdsQuery`), not a materialized list, so it
  stays one statement however many contacts match.
- Rules are validated by compiling them once at write time, so a broken segment
  is rejected with `422` instead of throwing on every read.

## Consequences

- Membership scales with the database, not with .NET memory, and is always
  evaluated against contacts as they are *now* — nothing to invalidate.
- A regression to in-memory filtering would still return the right rows and pass
  ordinary functional tests, so `SegmentSqlTranslationTests` asserts the
  generated SQL directly (via `ToQueryString`) — `json_extract` present, filter
  in the `WHERE`, conversation filter as a subquery.
- Every operator and field must be expressible in SQL; anything EF cannot
  translate must fail loudly rather than silently evaluate client-side. Rules are
  therefore a closed, validated grammar rather than arbitrary code.
- Attributes are stored as a flat string map so a comparison behaves the same
  whether the value arrived as `"5"` or `5`.
