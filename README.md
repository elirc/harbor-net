# harbor-net

An Intercom-style customer-messaging platform — C#/.NET backend only.

Workspaces, inboxes, contacts, teammates and teams, conversations with threaded
messages and internal notes, round-robin assignment, an SLA engine, reporting,
signed webhooks, an email channel, a help center, dynamic customer segments,
and search/filter endpoints throughout.

## Tech stack

- .NET 10 / ASP.NET Core Web API (controllers)
- EF Core 10 with SQLite
- xUnit — unit tests plus `WebApplicationFactory` integration tests against
  in-memory SQLite

## Solution layout

```
HarborNet.slnx
src/
  Harbor.Domain/          # entities, enums, domain behavior (no dependencies)
  Harbor.Infrastructure/  # EF Core DbContext, value converters, migrations, seeding
  Harbor.Api/             # ASP.NET Core Web API host, controllers, contracts
tests/
  Harbor.Tests/           # unit + integration tests
```

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/Harbor.Api
```

The API listens on `http://localhost:5230` (the `https` profile adds
`https://localhost:7287`). In Development the API migrates a local SQLite database (`harbor.db`) and seeds
a demo workspace ("Acme Support") with inboxes, teammates, teams, contacts,
tagged conversations, notes, an SLA-breached conversation, SLA policies, help
center articles, and customer segments. OpenAPI is served at `/openapi/v1.json`.

The seeded workspace ships with well-known development API keys:

| Key | Teammate | Role |
| --- | --- | --- |
| `hbk_dev_ada_admin` | Ada Lovelace | Admin |
| `hbk_dev_grace_agent` | Grace Hopper | Agent (capacity 5) |
| `hbk_dev_linus_agent` | Linus Pauling | Agent (away) |

## Authentication and authorization

Every endpoint requires an API key in `X-Api-Key`, except `/health`, workspace
bootstrap (`POST /api/workspaces`), and the public help center.

- Keys are minted once and shown once; only a SHA-256 digest is stored.
- `POST /api/workspaces` bootstraps the first admin and returns their key.
- **Roles**: `Admin` and `Agent`. Admins manage configuration (inboxes,
  teammates, teams, tags, canned replies, SLA policies, webhooks, help center,
  segments); agents read and work conversations.
- **Tenant isolation**: a `{workspaceId}` route that is not the caller's
  workspace is rejected with **403** without touching the database, so the
  response reveals nothing about whether it exists. A by-id resource belonging
  to another workspace is **404**.

## Domain model

- **Workspace** — top-level tenant; everything else hangs off it.
- **Inbox** — a channel inside a workspace. Optionally carries
  `firstResponseSlaMinutes`, an `emailAddress` (inbound routing), and
  `autoAssign` (round-robin).
- **Contact** — an end-user writing in, with custom `attributes`.
  **Teammate** — an agent, with availability and a capacity limit.
  **Team** — a group of teammates.
- **Conversation** — a thread between one contact and the workspace, living in
  one inbox. States: `Open`, `Snoozed` (with a future wake time), `Closed`.
  Assignable to exactly one teammate or team. Carries a priority and a channel.
- **Message** — `Reply` (visible to the contact) or `Note` (internal). The first
  teammate reply stamps `firstRespondedAt`; a contact reply on a snoozed/closed
  conversation reopens it; notes never change state or SLA.
- **SLA policy** — first-response and/or resolution targets scoped to an
  optional inbox and priority. **SLA breach event** — a missed target, recorded.
- **Webhook subscription** — an HMAC-signed egress path, with a delivery log.
- **Article / collection** — help-center content, `Draft` or `Published`.
- **Segment** — a dynamic group of contacts defined by rules, not membership.
- **Tag** — workspace-scoped label. **Canned reply** — saved response.

## API surface

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/health` | Health, version, and a database probe (503 when the DB is unreachable) |
| POST/GET | `/api/workspaces` | Bootstrap (returns admin API key) / list |
| GET | `/api/workspaces/{id}` | Workspace detail |
| POST/GET | `/api/workspaces/{id}/inboxes` | Create / list inboxes (SLA minutes, auto-assign, email address) |
| GET | `/api/workspaces/{id}/inboxes/{inboxId}` | Inbox detail |
| POST/GET | `/api/workspaces/{id}/contacts` | Create / list-search contacts (`?q=`) |
| GET/PUT/DELETE | `/api/contacts/{id}` | Contact detail / update / delete (409 with conversations) |
| POST/GET | `/api/workspaces/{id}/teammates` | Create (unique email, returns key) / list |
| GET | `/api/teammates/{id}` | Teammate detail |
| PUT | `/api/teammates/{id}/availability` | Set availability and capacity (self, or admin for anyone) |
| POST/GET | `/api/workspaces/{id}/teams` | Create (unique name) / list teams |
| GET | `/api/teams/{id}` | Team detail with member ids |
| POST | `/api/teams/{id}/members` | Add member (idempotent, same workspace only) |
| DELETE | `/api/teams/{id}/members/{teammateId}` | Remove member |
| POST | `/api/workspaces/{id}/conversations` | Start conversation with opening contact message |
| GET | `/api/workspaces/{id}/conversations` | List / filter / search (see below) |
| GET | `/api/conversations/{id}` | Conversation detail with full thread |
| POST | `/api/conversations/{id}/messages` | Add reply or internal note |
| POST | `/api/conversations/{id}/state` | Open / snooze (`snoozedUntil`) / close |
| POST | `/api/conversations/{id}/assignment` | Assign teammate or team, or unassign |
| GET | `/api/conversations/{id}/assignment-events` | Assignment audit trail |
| PUT | `/api/conversations/{id}/priority` | Set priority and re-stamp SLA targets |
| GET | `/api/conversations/{id}/sla-breaches` | SLA targets this conversation missed |
| GET | `/api/conversations/{id}/suggested-articles` | Published articles matching the conversation (`?limit=`) |
| POST/GET | `/api/workspaces/{id}/sla-policies` | Create / list policies (most specific first) |
| GET/PUT/DELETE | `/api/sla-policies/{id}` | Policy detail / update / delete |
| POST | `/api/workspaces/{id}/sla/evaluate` | Record breaches for overdue conversations (idempotent) |
| GET | `/api/workspaces/{id}/reports/volume` | Conversation volume (`?interval=Hour\|Day\|Week`) |
| GET | `/api/workspaces/{id}/reports/response-times` | Median/p90/p95 first-response and resolution |
| GET | `/api/workspaces/{id}/reports/teammates` | Per-teammate breakdown |
| GET | `/api/workspaces/{id}/reports/inboxes` | Per-inbox breakdown |
| GET | `/api/workspaces/{id}/reports/tags` | Tag distribution |
| POST/GET | `/api/workspaces/{id}/webhooks` | Create (returns signing secret once) / list |
| GET/PUT/DELETE | `/api/webhooks/{id}` | Subscription detail / update / delete |
| GET | `/api/webhooks/{id}/deliveries` | Delivery log |
| POST | `/api/workspaces/{id}/webhooks/dispatch` | Drain the delivery outbox (safe to repeat) |
| POST | `/api/workspaces/{id}/email/inbound` | Ingest a parsed inbound email |
| GET | `/api/messages/{id}/email` | Render a teammate reply as outbound email |
| POST/GET | `/api/workspaces/{id}/collections` | Create / list help-center collections |
| GET/PUT/DELETE | `/api/collections/{id}` | Collection detail / update / delete (409 if not empty) |
| POST/GET | `/api/workspaces/{id}/articles` | Create draft / list incl. drafts (`?status=`, `?collectionId=`, `?q=`) |
| GET/PUT/DELETE | `/api/articles/{id}` | Article detail / update / delete |
| POST | `/api/articles/{id}/publish` | Publish |
| POST | `/api/articles/{id}/unpublish` | Return to draft (keeps `publishedAt`) |
| POST/GET | `/api/workspaces/{id}/segments` | Create / list segments |
| GET/PUT/DELETE | `/api/segments/{id}` | Segment detail / update / delete |
| GET | `/api/segments/{id}/contacts` | Contacts currently matching the segment |
| POST/GET | `/api/workspaces/{id}/tags` | Create / list tags |
| DELETE | `/api/tags/{id}` | Delete tag (cascades off conversations) |
| PUT/DELETE | `/api/conversations/{id}/tags/{tagId}` | Tag / untag a conversation |
| POST/GET | `/api/workspaces/{id}/canned-replies` | Create / list-search canned replies (`?q=`) |
| GET/PUT/DELETE | `/api/canned-replies/{id}` | Canned reply detail / update / delete |

### Public help center (no API key)

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/public/workspaces/{id}/collections` | Collections that contain published articles |
| GET | `/api/public/workspaces/{id}/articles` | Published articles (`?q=`, `?collectionId=`) |
| GET | `/api/public/workspaces/{id}/articles/{slug}` | One published article |

Published articles only. A draft's slug returns 404 exactly like a slug that
never existed, so these endpoints cannot be used to probe for unpublished work.

## Pagination

Every list endpoint is paginated. `?page=` (1-based) and `?pageSize=` select the
page; totals come back in headers, so bodies stay plain arrays.

| Header | Meaning |
| --- | --- |
| `X-Total-Count` | Rows matching the query, before paging |
| `X-Page` / `X-Page-Size` | The page actually served |
| `X-Total-Pages` | Pages at this size |

Default page size is **50**, maximum **200**. Omitting the parameters selects the
first page rather than everything, so no list endpoint can return an unbounded
number of rows. Out-of-range input is clamped; a page past the end is an empty
page, not an error.

## Conversation filters

`GET /api/workspaces/{id}/conversations` accepts any combination of:

- `state=Open|Snoozed|Closed`
- `inboxId`, `contactId`, `assignedTeammateId`, `assignedTeamId`
- `unassigned=true` — nobody assigned
- `priority=Low|Normal|High|Urgent`
- `channel=Chat|Email`
- `segmentId=` — conversations whose contact currently matches the segment
- `tag=billing` — exact tag name, case-insensitive
- `q=invoice` — case-insensitive search across subject and reply bodies
  (internal notes are excluded)
- `slaBreached=true` — a first-response or resolution target was missed

Results are ordered by most recent activity.

## Assignment rules

An inbox with `autoAssign` round-robins new conversations across teammates who
are `Available` and under their `capacityLimit` (open and snoozed conversations
count; closing work frees a slot). The rotation pointer lives on the inbox, so it
is per-inbox and survives restarts. Nobody eligible leaves the conversation
unassigned. Every assignment change — automatic or manual — is recorded as an
`AssignmentEvent`.

## SLA engine

An SLA policy carries first-response and/or resolution targets scoped to an
optional inbox and priority; null means "any", so a policy with both null is the
workspace default. The most specific match wins (inbox outranks priority), and
an inbox with no matching policy falls back to its own `firstResponseSlaMinutes`.

The SLA clock always runs from the conversation's creation, so changing priority
moves the deadline rather than restarting it. Breaches are recorded when a reply
or close lands late, and `POST /api/workspaces/{id}/sla/evaluate` sweeps
conversations that are simply sitting overdue. Detection is idempotent.

## Webhooks

Subscribe to `conversation.created`, `conversation.assigned`,
`conversation.closed`, and `message.created`.

Deliveries are queued in the **same transaction** as the change that caused them
— a transactional outbox — so an event cannot outlive a rolled-back write, and a
write cannot commit while dropping its event. Nothing is sent inline;
`POST /api/workspaces/{id}/webhooks/dispatch` drains the outbox and retries with
exponential backoff (1/2/4/8 minutes, 5 attempts).

Payloads are signed with HMAC-SHA256 over `"{timestamp}.{body}"`:

```
X-Harbor-Signature: t=1784289115,v1=3a7f...
X-Harbor-Event: conversation.created
X-Harbor-Delivery: 6f1e...
```

The timestamp is bound into the signature so a captured request cannot be
replayed once it is older than the receiver's tolerance. The signing secret is
returned only when the subscription is created.

## Email channel

Harbor does not parse MIME. A mail provider posts already-parsed fields to
`POST /api/workspaces/{id}/email/inbound`, authenticating with an API key.

Mail is routed to an inbox by its `To` address, the sender is resolved to a
contact by email (created if new), and `In-Reply-To`/`References` are matched
against stored Message-IDs to thread onto an existing conversation — otherwise a
new one starts. An emailed reply reopens a closed conversation like any other.
Teammate replies on email conversations are stamped with the Message-ID they will
carry, so the customer's reply threads back.

## Segments

Segment rules are stored as JSON and compiled into a query, so membership is
evaluated by the database and is always live — a contact joins or leaves the
instant their attributes change.

```json
{
  "match": "All",
  "conditions": [
    { "field": "attributes.plan", "operator": "Equals", "value": "enterprise" },
    { "field": "lastSeenAt", "operator": "After", "value": "2026-01-01T00:00:00Z" }
  ]
}
```

Fields: `name`, `email`, `externalId`, `createdAt`, `lastSeenAt`, and
`attributes.<key>` (read with SQLite's `json_extract`). Operators: `Equals`,
`NotEquals`, `Contains`, `NotContains`, `StartsWith`, `EndsWith`, `Exists`,
`NotExists`, and `Before`/`After` on dates. Negative operators are true for
contacts that lack the field entirely.

## Error contract

Errors are RFC 7807 `application/problem+json`:

| Status | Meaning |
| --- | --- |
| 400 | Validation and malformed input |
| 401 | Missing or unknown API key |
| 403 | Wrong workspace, or an admin-only endpoint |
| 404 | Unknown resource, or one in another workspace |
| 409 | Uniqueness conflicts, and concurrent modification |
| 422 | Domain-rule violations (cross-workspace references, snoozing into the past, ambiguous assignment, a note authored by a contact, unusable segment rules) |
| 503 | `/health` when the database is unreachable |

Every response carries `X-Request-Id`, echoing an upstream value when present, so
a report can be correlated with the logs.

## Concurrency

Conversations and webhook deliveries carry an optimistic-concurrency token.
SQLite has no `rowversion`, so it is an ordinary `Guid` column rolled on every
update in `SaveChanges`; EF puts the original value in the UPDATE's `WHERE`, so a
writer working from a stale copy matches no row and gets **409** instead of
silently overwriting whoever got there first.

The token is deliberately **not** on the inbox's round-robin pointer: a lost
update there only makes the rotation slightly uneven, and failing a customer's
conversation to protect rotation fairness is a bad trade.

## SQLite & DateTimeOffset

SQLite cannot order or compare `DateTimeOffset` natively. Every `DateTimeOffset`
property is stored as its **UTC tick count** (`long`) via a `ValueConverter`
applied in `ConfigureConventions`, so `ORDER BY` / comparisons are always
chronological regardless of the original offset. Values round-trip normalized to
UTC.

## Tests

```bash
dotnet test
```

644 tests: domain unit tests (state machine, SLA rules, percentile maths, HMAC
signing, email rendering, keyword matching, segment compilation, converter
semantics, seeder idempotence) and end-to-end integration tests over every
endpoint via `WebApplicationFactory` + in-memory SQLite. A migration-drift guard
(`MigrationDriftTests`) fails the build when the EF model and the migrations
disagree. See [docs/testing.md](docs/testing.md).

## Documentation

Full documentation lives in [`docs/`](docs/):

- [architecture.md](docs/architecture.md) — layering, the conversation state
  machine, `ConversationStarter`, the SLA engine, assignment, the webhook outbox
  and signing, segments-as-SQL, the UTC-ticks converter, pagination.
- [api-reference.md](docs/api-reference.md) — every endpoint: method, route,
  auth, request/response shapes, error codes.
- [getting-started.md](docs/getting-started.md) — run, seed, and a verified curl
  walkthrough (chat and email).
- [adr/](docs/adr/README.md) — the decisions behind the design.
- [testing.md](docs/testing.md) — test taxonomy, harness, and the drift guard.
