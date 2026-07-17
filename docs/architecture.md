# Architecture

Harbor is an Intercom-style customer-messaging backend: workspaces receive
messages from contacts over chat and email, route them into inboxes, assign them
to teammates, hold them to SLA targets, and emit signed webhooks. This document
explains how the pieces fit and why the load-bearing decisions were made the way
they were. Each decision also has a short [ADR](adr/README.md).

## Layering

Three projects, dependencies pointing inward only:

```
Harbor.Api  ──▶  Harbor.Infrastructure  ──▶  Harbor.Domain
(controllers,        (EF Core DbContext,          (entities, enums,
 contracts,           value converters,            domain behavior;
 auth, filters)       migrations, seeding,         no dependencies)
                      SLA/segment/webhook logic)
```

- **Harbor.Domain** holds the entities and the behavior that belongs to them:
  the conversation state machine, SLA target arithmetic, breach predicates.
  It references nothing, so the rules can be unit-tested without a database or
  a web host. See `Conversation.cs` for the richest example — `Open`, `Snooze`,
  `Close`, `RegisterMessage`, and the `Is*Breached` predicates all live on the
  entity.
- **Harbor.Infrastructure** owns persistence (`HarborDbContext`), the migrations,
  the seeder, and the services that need the database to do their job:
  `SlaPolicies` (resolution), `AutoAssigner` (round-robin), `SegmentCompiler`
  (rules → SQL), `Webhooks`/`WebhookDispatcher`/`WebhookSender` (the outbox),
  and the `UtcDateTimeOffsetConverter`.
- **Harbor.Api** is the ASP.NET Core host: controllers, request/response
  contracts, the API-key authentication handler, the workspace-scope
  authorization filter, exception handlers, pagination, and request logging.

The one deliberate cross-cutting seam in the API layer is
`ConversationStarter`, described next.

## The conversation state machine

A conversation is a thread between one contact and the workspace, living in one
inbox. It has three states:

```
        ┌─────────── contact reply ───────────┐
        │                                      │
        ▼                                      │
     ┌──────┐   snooze(until)   ┌─────────┐    │
     │ Open │ ────────────────▶ │ Snoozed │    │
     │      │ ◀──────────────── │         │    │
     └──────┘      open         └─────────┘    │
        │ ▲                          │         │
  close │ │ open / contact reply     │ close   │
        ▼ │                          ▼         │
     ┌────────┐ ◀────────────────────┘         │
     │ Closed │ ───────── contact reply ───────┘
     └────────┘
```

State transitions are permissive by design — any state can move to any other —
but the *metadata* is always kept consistent by the entity methods on
`Conversation`:

- `Open(now)` clears `SnoozedUntil` and `ClosedAt`.
- `Snooze(until, now)` requires `until` to be in the future (a `DomainException`
  otherwise → 422) and clears `ClosedAt`.
- `Close(now)` sets `ClosedAt`, clears `SnoozedUntil`, and stamps
  `FirstResolvedAt` **once** (never overwritten by a later re-close).
- `RegisterMessage(message, now)` is where the interesting rules live:
  - A **contact** message on a non-Open conversation reopens it (clearing snooze
    and close metadata). This is the auto-reopen: a customer writing back always
    lands in the queue.
  - The **first teammate reply** stamps `FirstRespondedAt` (used by the SLA
    engine). Subsequent replies leave it unchanged.
  - A **note** (`MessageKind.Note`) bumps `LastMessageAt` but never changes
    state and never counts as a first response — it is internal staff context,
    invisible to the SLA clock and to the customer.

Because these rules live on the entity rather than in a controller, chat replies,
emailed replies, and any future channel all get identical behavior for free.

## ConversationStarter — the single creation path

Every conversation is born in `ConversationStarter.StartAsync`
(`src/Harbor.Api/Infrastructure/ConversationStarter.cs`). Chat
(`POST /api/workspaces/{id}/conversations`) and inbound email
(`POST /api/workspaces/{id}/email/inbound`) both call it. In one place it:

1. builds the conversation and its opening contact message;
2. stamps SLA targets via `SlaPolicies.ApplyAsync`;
3. round-robin auto-assigns if the inbox has `AutoAssign` and someone is
   eligible, recording an `AssignmentEvent`;
4. queues the `conversation.created` webhook, and `conversation.assigned` if it
   was assigned.

The caller performs the single `SaveChangesAsync`, so the conversation, its
assignment event, and its webhook deliveries all commit together.

This is a deliberate anti-duplication measure. The alternative — each channel's
controller assembling a conversation itself — is exactly how an email
conversation ends up silently skipping auto-assignment or SLA stamping because
the second code path forgot a step. Any new creation path **must** go through
`ConversationStarter`. See [ADR-0009](adr/0009-single-creation-path.md).

## SLA engine

An **SLA policy** (`SlaPolicy`) carries a first-response and/or resolution
target, scoped to an optional inbox and an optional priority. A null scope means
"any", so a policy with both null is the workspace-wide default.

**Resolution** (`SlaPolicies.Resolve`) picks the most specific matching policy.
Specificity weights inbox above priority (`Specificity = (inbox?2:0) + (priority?1:0)`),
so an inbox-specific policy beats a priority-wide one; ties break by creation
order (oldest wins) then id, keeping resolution deterministic. When no policy
matches, the conversation falls back to the inbox's own `FirstResponseSlaMinutes`
— which keeps inboxes configured before SLA policies existed working unchanged.

**Targets** are stamped by `Conversation.ApplySlaTargets`, always measured from
`CreatedAt`. This is the key design choice: the clock runs from creation, so
changing priority mid-conversation *moves* the deadline rather than restarting
it. Escalating an old conversation to Urgent can therefore put it immediately
past due.

**Breach detection** (`SlaBreaches.DetectAsync`) is idempotent — one
`SlaBreachEvent` per conversation per kind, enforced by a unique index — so the
same code runs from three places without duplicating history:

- the reply path, catching a first reply that lands after the target;
- the close path, catching a close that lands after the resolution target;
- `POST /api/workspaces/{id}/sla/evaluate`, a sweep over conversations that are
  simply sitting overdue with nothing happening to them.

The breach predicates are strict (`now > due`), so the target instant itself is
still on time. First-response and resolution are judged **independently** and
each on the *first* event: `IsFirstResponseBreached` looks at `FirstRespondedAt`,
`IsResolutionBreached` at `FirstResolvedAt`. Neither is cleared by a reopen, so
reopening a conversation that was answered and closed on time never
retroactively breaches it.

## Assignment design

An inbox with `AutoAssign` round-robins new conversations across teammates who
are `Available` and under their `CapacityLimit`. `AutoAssigner.PickNextAsync`:

1. lists available teammates ordered by `CreatedAt` then `Id` (a stable ring);
2. counts each teammate's open + snoozed assigned conversations (only `Closed`
   frees a slot) and drops anyone at capacity;
3. continues the rotation *after* `Inbox.LastAssignedTeammateId` — skipping
   past a previous assignee who has since become ineligible — and wraps around;
4. advances `Inbox.LastAssignedTeammateId` to whoever it picked.

The rotation pointer lives on the inbox, so it is per-inbox and survives
restarts. If nobody is eligible the conversation is simply left unassigned (it
still exists, its SLA still runs). Every assignment change, automatic or manual,
is recorded as an `AssignmentEvent` with actor/from/to ids that are intentionally
unconstrained — the audit trail must survive a teammate being deleted.

### The deliberate no-token round-robin

`Inbox.LastAssignedTeammateId` has **no** optimistic-concurrency token, unlike
`Conversation` and `WebhookDelivery`. This is a considered trade, not an
oversight. If two conversations arrive at the same instant and both read the
pointer before either writes it, one update is lost and the rotation repeats a
teammate. The cost is that someone gets one extra conversation. The alternative —
a token that makes the second write fail with a 409 — would mean *failing a
customer's incoming conversation* to protect rotation fairness. That is a bad
trade: an uneven rotation self-corrects on the next assignment; a rejected
conversation does not. See [ADR-0006](adr/0006-no-token-round-robin.md).

## Webhook outbox and signing

Webhooks use a **transactional outbox**. `Webhooks.PublishAsync` only *queues*
`WebhookDelivery` rows into the change tracker; the caller's `SaveChangesAsync`
commits them alongside the change that caused them. Nothing is ever sent inline
during a request. This gives the outbox its defining property:

> An event exists exactly when the change that caused it exists — no event for a
> rolled-back write, no committed change that silently dropped its event.

An inline HTTP call would break both halves: it would tie request latency to the
subscriber's, and lose the event entirely if the process died between the commit
and the send.

Delivery is a separate step. `POST /api/workspaces/{id}/webhooks/dispatch`
(a scheduler would call this on a timer) runs `WebhookDispatcher.DispatchAsync`,
which sends every pending delivery whose `NextAttemptAt` has arrived and records
the outcome. Failures retry with exponential backoff (1, 2, 4, 8 minutes) up to
`WebhookDelivery.MaxAttempts` (5), then are marked `Failed`. Deactivating a
subscription pauses delivery without burning attempts.

The payload stored on the delivery is the exact JSON that will be sent, byte for
byte, serialized once at publish time with pinned options
(`Webhooks.PayloadJson`). It is *not* re-rendered at send time — doing so would
rewrite history (a conversation that changed after the event was queued) and, worse,
invalidate the signature.

**Signing** (`WebhookSigner`) is HMAC-SHA256 over `"{timestamp}.{body}"`, not
the body alone. The timestamp travels in the header:

```
X-Harbor-Signature: t=1784289115,v1=3a7f...
X-Harbor-Event: conversation.created
X-Harbor-Delivery: 6f1e...
```

Binding the timestamp into the signed string is what stops replay: a receiver
rejects anything older than its tolerance, and an attacker cannot re-stamp a
captured request without the secret. The secret is returned only once, when the
subscription is created. See [ADR-0003](adr/0003-transactional-outbox.md) and
[ADR-0004](adr/0004-timestamp-body-signing.md).

## Segments compile to SQL

A **segment** is a dynamic group of contacts defined by rules
(`SegmentRuleSet`), not by stored membership. `SegmentCompiler.Compile` turns
the rules into an `Expression<Func<Contact, bool>>` that EF Core translates to a
SQL `WHERE` clause. Built-in fields (`name`, `email`, `externalId`, `createdAt`,
`lastSeenAt`) map to columns; custom attributes (`attributes.<key>`) reach into
the contact's JSON column through SQLite's `json_extract`, exposed to EF as a
`[DbFunction]` on `HarborDbContext`.

This must stay a query. A segment over a million contacts is a `WHERE` clause,
not a million objects loaded and filtered in memory — and the conversation
filter (`?segmentId=`) composes it as a subquery over contact ids, never a
materialized id list. Membership is therefore always live: a contact joins or
leaves the instant its attributes change, with nothing to refresh. Broken rules
are rejected at write time (422) by compiling them once during validation, so a
bad segment never throws on every read. See
[ADR-0005](adr/0005-segments-as-sql.md).

## UtcDateTimeOffsetConverter

SQLite has no native `DateTimeOffset` type and cannot order or compare one
correctly. Every `DateTimeOffset` property is stored as its **UTC tick count**
(a `long`) via `UtcDateTimeOffsetConverter`, applied globally in
`HarborDbContext.ConfigureConventions`. This makes `ORDER BY` and range
comparisons chronological regardless of the original UTC offset, and values
round-trip normalized to UTC. Because it is a convention rather than a
per-property mapping, no entity has to remember to opt in. See
[ADR-0008](adr/0008-utc-ticks-converter.md).

## Pagination

Every list endpoint is paginated. The page selection travels in the query string
(`?page=`, 1-based; `?pageSize=`) and the totals come back in response headers,
so bodies stay plain arrays and no existing client had to change when pagination
arrived:

| Header | Meaning |
| --- | --- |
| `X-Total-Count` | Rows matching the query, before paging |
| `X-Page` / `X-Page-Size` | The page actually served |
| `X-Total-Pages` | Pages at this size (at least 1) |

Default page size is 50, maximum 200 (`Paging.DefaultPageSize` /
`Paging.MaxPageSize`). The production-relevant property is that omitting the
parameters selects the *first page*, not everything — no list endpoint can
return an unbounded number of rows. Out-of-range input is clamped rather than
rejected: a page past the end is an empty page, which is what a client walking
pages expects. See [ADR-0007](adr/0007-header-pagination.md).

> **Binding note.** The query parameter is named `paging`, not `page`. Binding a
> complex `[FromQuery] PageRequest page` collided with the object's own `Page`
> property and silently ignored `?page=` — every list looked correct because
> they all asserted page one. `tests/.../PaginationBoundaryTests.cs` guards
> against a regression by walking to a real second page on every list endpoint.

## Authentication and authorization

- **Authentication** (`ApiKeyAuthenticationHandler`): the `X-Api-Key` header is
  SHA-256 hashed and matched against `Teammate.ApiKeyHash`. Keys are minted once,
  shown once, and only the digest is stored. The resulting principal carries the
  teammate id, workspace id, and role. A missing or unknown key is **401**.
- **Roles**: `Admin` and `Agent`. Admins manage configuration; agents read and
  work conversations. Enforced per-endpoint with `[Authorize(Roles = "Admin")]`.
- **Tenant isolation** (`WorkspaceScopeFilter`, registered globally): a request
  whose `{workspaceId}` route value is not the caller's workspace is rejected
  with **403** without touching the database. A by-id resource in another
  workspace is **404**, because the lookup is scoped and a row you may not see
  simply is not there. This 403-vs-404 split is intentional and pinned by tests;
  see [ADR-0002](adr/0002-403-vs-404.md).

Anonymous endpoints are exactly `/health`, workspace bootstrap
(`POST /api/workspaces`), and the public help center
(`/api/public/workspaces/{id}/...`).

## Concurrency

`Conversation` and `WebhookDelivery` implement `IHasVersion` — a `Guid` column
marked as a concurrency token, rolled on every modification in
`HarborDbContext.SaveChanges`. SQLite has no `rowversion`; EF puts the token's
original value in the `UPDATE`'s `WHERE` clause and the new value in `SET`, so a
writer working from a stale copy matches no row and gets a
`DbUpdateConcurrencyException`, which `ConcurrencyExceptionHandler` maps to
**409**. These are the two things people genuinely fight over: two agents
grabbing or closing the same conversation, and two dispatchers draining the same
delivery. The round-robin pointer deliberately has no token (above).

## Error contract

Errors are RFC 7807 `application/problem+json`, produced by
`AddProblemDetails()` plus two exception handlers:

| Status | Cause |
| --- | --- |
| 400 | Model-validation failure, malformed JSON, unparseable query values |
| 401 | Missing or unknown API key |
| 403 | Foreign workspace, or an admin-only endpoint called by an agent |
| 404 | Unknown resource, or one in another workspace |
| 409 | Uniqueness conflict, or concurrent modification |
| 422 | Domain-rule violation (see below) |
| 503 | `/health` when the database probe fails |

422 covers `DomainException`s (e.g. snoozing into the past) and controller-level
domain problems: cross-workspace references, ambiguous assignment
(both teammate and team), a note authored by a contact, an SLA policy with no
targets, unusable segment rules, an unknown inbox address on inbound email, a
report interval too fine. Every response carries `X-Request-Id`, echoing an
upstream value when present.

## Startup and the EnsureCreated/Migrate gap

`Program.cs` runs `db.Database.Migrate()` then `DataSeeder.Seed(db)` — but only
in the Development environment. The integration test harness
(`HarborApiFactory`), by contrast, builds its schema with `EnsureCreated()`
straight from the model. These two ways of building the schema can disagree: a
model change with no matching migration is invisible to `EnsureCreated()` but
breaks `Migrate()` against an existing `harbor.db`. This bit once in Sprint 08.
The `MigrationDriftTests` guard closes the gap by diffing the model against the
migrations directly; see [testing.md](testing.md).
