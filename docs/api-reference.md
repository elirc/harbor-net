# API reference

Every endpoint, its authorization, its shapes, and the status codes it returns.
Routes and codes here were read from the controllers in `src/Harbor.Api`, not
from prose — where behavior is subtle the controller is named.

## Conventions

- **Base**: all routes are relative to the host (dev: `http://localhost:5230`).
- **Auth column**:
  - _Anonymous_ — no API key.
  - _Any_ — any authenticated teammate (Agent or Admin).
  - _Admin_ — `[Authorize(Roles = "Admin")]`.
  - _Self/Admin_ — the teammate themselves, or an admin acting on anyone.
- **Enums** serialize as strings. `WebhookEventType` uses dotted wire names
  (`conversation.created`); all others use their member name (`Open`, `Urgent`,
  `Available`, `Draft`, …).
- **Errors** are RFC 7807 `application/problem+json`. The status table in each
  section lists only the codes that endpoint can produce beyond the universal
  ones: **401** (missing/unknown key) on every non-anonymous route, **403** on
  every `{workspaceId}`-scoped route called for a foreign workspace, and **400**
  for malformed input or a bad query value.
- **Pagination**: every list endpoint accepts `?page=` (1-based) and
  `?pageSize=` (default 50, max 200) and returns `X-Total-Count`, `X-Page`,
  `X-Page-Size`, `X-Total-Pages` headers. Bodies are plain arrays.

## Enumerations

| Enum | Values |
| --- | --- |
| `ConversationState` | `Open`, `Snoozed`, `Closed` |
| `ConversationPriority` | `Low`, `Normal`, `High`, `Urgent` |
| `MessageKind` | `Reply`, `Note` |
| `AuthorType` | `Contact`, `Teammate` |
| `MessageChannel` | `Chat`, `Email` |
| `TeammateRole` | `Agent`, `Admin` |
| `TeammateAvailability` | `Available`, `Away` |
| `ArticleStatus` | `Draft`, `Published` |
| `AssignmentKind` | `Auto`, `Manual` |
| `SlaBreachKind` | `FirstResponse`, `Resolution` |
| `WebhookEventType` | `conversation.created`, `conversation.assigned`, `conversation.closed`, `message.created` |
| `WebhookDeliveryStatus` | `Pending`, `Succeeded`, `Failed` |
| `SegmentMatch` | `All`, `Any` |
| `SegmentOperator` | `Equals`, `NotEquals`, `Contains`, `NotContains`, `StartsWith`, `EndsWith`, `Exists`, `NotExists`, `Before`, `After` |
| `ReportInterval` | `Hour`, `Day`, `Week` |

## Health

| Method | Route | Auth |
| --- | --- | --- |
| GET | `/health` | Anonymous |

Runs a `SELECT 1` database probe. Returns **200** when healthy, **503** when the
probe fails.

```json
{
  "status": "ok",
  "name": "harbor-net",
  "version": "1.0.0+<commit-sha>",
  "utcNow": "2026-07-17T15:38:20.71+00:00",
  "database": { "healthy": true, "durationMs": 120, "error": null }
}
```

`version` is the assembly informational version. When the database is
unreachable, `status` is `"unhealthy"`, `database.healthy` is `false`, and
`database.error` is the string `"unreachable"` (details go to the operator's
logs, not the response).

## Workspaces

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces` | Anonymous | Bootstrap; returns the first admin's API key |
| GET | `/api/workspaces` | Any | Lists the caller's own workspace |
| GET | `/api/workspaces/{workspaceId}` | Any | Workspace detail |

`POST` body: `{ "name", "adminName", "adminEmail" }`. Response
(`CreateWorkspaceResponse`): `{ "workspace": {...}, "admin": {...}, "apiKey" }`.
**The `apiKey` is shown only here.** → **201**.

`WorkspaceResponse`: `{ id, name, createdAt }`.

## Inboxes

| Method | Route | Auth |
| --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/inboxes` | Admin |
| GET | `/api/workspaces/{workspaceId}/inboxes` | Any |
| GET | `/api/workspaces/{workspaceId}/inboxes/{id}` | Any |

`POST` body (`CreateInboxRequest`): `{ "name", "firstResponseSlaMinutes"?,
"autoAssign"?=false, "emailAddress"? }`. `emailAddress` must be unique within the
workspace → **409** on collision (case-insensitive); an invalid address → **400**.
`InboxResponse`: `{ id, workspaceId, name, firstResponseSlaMinutes, autoAssign,
emailAddress, createdAt }`. Create → **201**.

## Contacts

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/contacts` | Any | |
| GET | `/api/workspaces/{workspaceId}/contacts` | Any | `?q=` searches name/email |
| GET | `/api/contacts/{id}` | Any | |
| PUT | `/api/contacts/{id}` | Any | |
| DELETE | `/api/contacts/{id}` | Admin | **409** if the contact has conversations |

`POST`/`PUT` body: `{ "name", "email"?, "externalId"?, "attributes"? }` where
`attributes` is a flat `{ "key": "value" }` map (null values are dropped).
`ContactResponse`: `{ id, workspaceId, name, email, externalId, attributes,
createdAt, lastSeenAt }`. Create → **201**.

## Teammates

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/teammates` | Admin | Unique email; returns API key |
| GET | `/api/workspaces/{workspaceId}/teammates` | Any | |
| GET | `/api/teammates/{id}` | Any | |
| PUT | `/api/teammates/{id}/availability` | Self/Admin | |

`POST` body: `{ "name", "email", "role"?=Agent }`. Duplicate email → **409**.
Response (`TeammateCreatedResponse`): `{ id, workspaceId, name, email, role,
createdAt, apiKey }` — **the `apiKey` is shown only here** → **201**.

`PUT availability` body: `{ "availability", "capacityLimit"? }`. An agent may set
their own; setting another teammate's when not an admin → **403**
(`TeammatesController.UpdateAvailability`).

`TeammateResponse`: `{ id, workspaceId, name, email, role, availability,
capacityLimit, createdAt }` — no key field.

## Teams

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/teams` | Admin | Unique name → **409** |
| GET | `/api/workspaces/{workspaceId}/teams` | Any | |
| GET | `/api/teams/{id}` | Any | |
| POST | `/api/teams/{id}/members` | Admin | Idempotent; foreign teammate → **422** |
| DELETE | `/api/teams/{id}/members/{teammateId}` | Admin | |

`TeamResponse`: `{ id, workspaceId, name, memberIds, createdAt }`.
Add-member body: `{ "teammateId" }`.

## Conversations

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/conversations` | Any | Starts via `ConversationStarter` |
| GET | `/api/workspaces/{workspaceId}/conversations` | Any | Filters below |
| GET | `/api/conversations/{id}` | Any | Full thread |
| POST | `/api/conversations/{id}/messages` | Any | Reply or note |
| POST | `/api/conversations/{id}/state` | Any | Open / snooze / close |
| POST | `/api/conversations/{id}/assignment` | Any | Assign / unassign |
| GET | `/api/conversations/{id}/assignment-events` | Any | Audit trail |
| PUT | `/api/conversations/{id}/priority` | Any | Re-stamps SLA |
| GET | `/api/conversations/{id}/sla-breaches` | Any | |
| GET | `/api/conversations/{id}/suggested-articles` | Any | `?limit=`, default 5 |

### Start

Body (`StartConversationRequest`): `{ "inboxId", "contactId", "subject"?,
"body", "priority"? }`. Unknown inbox or contact in this workspace → **422**.
Returns `ConversationDetailResponse` → **201**. Auto-assign, SLA stamping, and
webhooks all fire here.

### List filters

`GET /api/workspaces/{workspaceId}/conversations` accepts any combination of:

| Param | Meaning |
| --- | --- |
| `state` | `Open` / `Snoozed` / `Closed` |
| `inboxId`, `contactId`, `assignedTeammateId`, `assignedTeamId` | Exact match |
| `unassigned=true` | Neither teammate nor team assigned |
| `priority` | `Low` / `Normal` / `High` / `Urgent` |
| `channel` | `Chat` / `Email` |
| `segmentId` | Conversations whose contact currently matches the segment (unknown segment → **422**) |
| `tag` | Exact tag name, case-insensitive |
| `q` | Case-insensitive search over subject and reply bodies (notes excluded) |
| `slaBreached=true` | A first-response or resolution target was missed |

Ordered by most recent activity (`LastMessageAt` descending).

### Add message

Body (`AddMessageRequest`): `{ "authorType", "authorId", "kind", "body" }`.

- A `Contact` author must be the conversation's own contact → else **422**
  (`Invalid contact author`).
- A `Teammate` author must belong to this workspace → else **422**
  (`Unknown teammate`).
- A `Note` may only be authored by a teammate → else **422**
  (`Invalid note author`).
- On an email conversation a `Reply` is stamped with a Message-ID and channel
  `Email`; a note stays on chat.

Returns `MessageResponse` → **201**. A late first reply records a breach here.

### Change state

Body (`ChangeStateRequest`): `{ "state", "snoozedUntil"? }`.

- `Snoozed` without `snoozedUntil` → **422** (`Missing snooze time`).
- `snoozedUntil` in the past → **422** (`Domain rule violated`,
  "Snooze time must be in the future.").
- An unknown numeric state → **422** (`Unknown state`).

Returns `ConversationSummaryResponse` → **200**. A `Closed` state queues a
`conversation.closed` webhook and can record a resolution breach.

### Assignment

Body (`AssignConversationRequest`): `{ "teammateId"?, "teamId"? }`. Provide
exactly one, or neither to unassign.

- Both provided → **422** (`Ambiguous assignment`).
- A teammate or team not in this workspace → **422**.

Every call records an `AssignmentEvent` (`Manual`); assigning (not unassigning)
queues a `conversation.assigned` webhook. `AssignmentEventResponse`:
`{ id, conversationId, kind, actorTeammateId, fromTeammateId, fromTeamId,
toTeammateId, toTeamId, createdAt }`.

### Priority

Body (`SetPriorityRequest`): `{ "priority" }`. Re-stamps SLA targets from the
policy now governing the conversation, measured from creation — so escalating an
old conversation can put it immediately past due, recording a breach.

### `ConversationSummaryResponse` / `ConversationDetailResponse`

Summary: `{ id, workspaceId, inboxId, contactId, subject, state, snoozedUntil,
closedAt, assignedTeammateId, assignedTeamId, priority, channel,
firstResponseDueAt, firstRespondedAt, resolutionDueAt, firstResolvedAt,
slaPolicyId, tags[], createdAt, updatedAt, lastMessageAt }`. Detail adds
`messages[]` (`MessageResponse`, oldest first).

## SLA policies & evaluation

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/sla-policies` | Admin | |
| GET | `/api/workspaces/{workspaceId}/sla-policies` | Any | Most specific first |
| GET | `/api/sla-policies/{id}` | Any | |
| PUT | `/api/sla-policies/{id}` | Admin | |
| DELETE | `/api/sla-policies/{id}` | Admin | |
| POST | `/api/workspaces/{workspaceId}/sla/evaluate` | Any | Sweep overdue; idempotent |

`POST`/`PUT` body: `{ "name", "inboxId"?, "priority"?, "firstResponseMinutes"?,
"resolutionMinutes"? }`.

- No target set → **422** (`SLA policy has no targets`).
- `inboxId` not in this workspace → **422** (`Unknown inbox`).
- A duplicate `(inboxId, priority)` scope → **409** (`Duplicate SLA policy scope`).

`SlaPolicyResponse`: `{ id, workspaceId, name, inboxId, priority,
firstResponseMinutes, resolutionMinutes, createdAt }`.
`SlaBreachEventResponse`: `{ id, conversationId, kind, dueAt, breachedAt,
slaPolicyId, createdAt }`.

## Reports

All under `/api/workspaces/{workspaceId}/reports/`, auth _Any_, sharing a
`ReportFilterRequest` query: `from`, `to` (bound conversation creation,
inclusive/exclusive), `inboxId`, `assignedTeammateId`, `assignedTeamId`,
`priority`, `tag`.

| Method | Route | Extra | Returns |
| --- | --- | --- | --- |
| GET | `reports/volume` | `?interval=Hour\|Day\|Week` (default `Day`) | `VolumeReportResponse` |
| GET | `reports/response-times` | | `ResponseTimeReportResponse` |
| GET | `reports/teammates` | | `BreakdownRowResponse[]` |
| GET | `reports/inboxes` | | `BreakdownRowResponse[]` |
| GET | `reports/tags` | | `TagDistributionResponse` |

Timings (`DurationStatsResponse`: `{ count, p50Minutes, p90Minutes, p95Minutes,
averageMinutes }`) are computed in memory over the materialized slice; null
percentiles mean an empty sample. A volume request whose range needs more than
1000 buckets → **422** (`Report interval too fine`).

## Webhooks

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/webhooks` | Admin | Returns secret once |
| GET | `/api/workspaces/{workspaceId}/webhooks` | Admin | |
| GET | `/api/webhooks/{id}` | Admin | |
| PUT | `/api/webhooks/{id}` | Admin | |
| DELETE | `/api/webhooks/{id}` | Admin | |
| GET | `/api/webhooks/{id}/deliveries` | Admin | Newest first |
| POST | `/api/workspaces/{workspaceId}/webhooks/dispatch` | Admin | Drain outbox |

`POST` body (`CreateWebhookRequest`): `{ "url", "events" }` — `url` must be a
valid URL and `events` non-empty, else **400**. Response
(`WebhookCreatedResponse`) includes `secret` (**shown only here**) → **201**.
`PUT` body adds `"isActive"`. `WebhookResponse` (and delivery payloads) never
include the secret.

`WebhookDeliveryResponse`: `{ id, subscriptionId, eventType, status,
attemptCount, lastAttemptAt, nextAttemptAt, responseStatusCode, error,
deliveredAt, createdAt, payload }`. Dispatch retries failures with backoff
(1/2/4/8 min) up to 5 attempts. Delivery requests carry `X-Harbor-Signature`
(`t=<unix>,v1=<hmac>`), `X-Harbor-Event`, `X-Harbor-Delivery`.

## Email channel

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/email/inbound` | Any | Ingest parsed mail |
| GET | `/api/messages/{id}/email` | Any | Render a reply as outbound email |

Inbound body (`InboundEmailRequest`): `{ "from", "fromName"?, "to", "subject"?,
"body", "messageId"?, "inReplyTo"?, "references"? }`. Routed to an inbox by `to`
(unknown address → **422**, `Unknown inbox address`); the sender is resolved to a
contact by email (created if new); `inReplyTo`/`references` thread onto an
existing conversation, else a new one starts through `ConversationStarter`.
Response (`InboundEmailResponse`): `{ conversationId, messageId, contactId,
startedNewConversation, createdContact }`.

`GET .../email` renders a teammate reply as the outbound email
(`RenderedEmailResponse`: `{ messageId, emailMessageId, from, to, subject,
inReplyTo, references[], body }`). A note, a chat-only inbox, or a contact's own
message → **422**.

## Help center (authoring)

Auth _Admin_ to write, _Any_ to read.

| Method | Route | Notes |
| --- | --- | --- |
| POST/GET | `/api/workspaces/{workspaceId}/collections` | |
| GET/PUT/DELETE | `/api/collections/{id}` | Delete with articles → **409** |
| POST/GET | `/api/workspaces/{workspaceId}/articles` | List `?status=`, `?collectionId=`, `?q=` |
| GET/PUT/DELETE | `/api/articles/{id}` | |
| POST | `/api/articles/{id}/publish` | |
| POST | `/api/articles/{id}/unpublish` | Returns to draft, keeps `publishedAt` |

Collections/articles derive a slug from the name/title when none is given;
duplicate slug in a workspace → **409**; an unusable slug → **422**; an unknown
collection on an article → **422**. Articles are created as **drafts**.
`CollectionResponse`: `{ id, workspaceId, name, slug, description,
publishedArticles, createdAt }`. `ArticleResponse`: `{ id, workspaceId,
collectionId, title, slug, body, status, publishedAt, createdAt, updatedAt }`.

## Public help center (anonymous)

| Method | Route |
| --- | --- |
| GET | `/api/public/workspaces/{workspaceId}/collections` |
| GET | `/api/public/workspaces/{workspaceId}/articles` (`?q=`, `?collectionId=`) |
| GET | `/api/public/workspaces/{workspaceId}/articles/{slug}` |

Published articles only; collections with nothing published are hidden. A draft's
slug returns **404** exactly like a slug that never existed, so these endpoints
cannot probe for unpublished work. `PublicArticleResponse`: `{ id, collectionId,
title, slug, body, publishedAt, updatedAt }`.

## Segments

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/segments` | Admin | |
| GET | `/api/workspaces/{workspaceId}/segments` | Any | |
| GET | `/api/segments/{id}` | Any | |
| PUT | `/api/segments/{id}` | Admin | |
| DELETE | `/api/segments/{id}` | Admin | |
| GET | `/api/segments/{id}/contacts` | Any | Contacts currently matching |

`POST`/`PUT` body: `{ "name", "rules" }` where `rules` is
`{ "match": "All"|"Any", "conditions": [ { "field", "operator", "value"? } ] }`.
Fields: `name`, `email`, `externalId`, `createdAt`, `lastSeenAt`,
`attributes.<key>`. Unusable rules → **422** (`Invalid segment rules`); a
duplicate name → **409**. `SegmentResponse`: `{ id, workspaceId, name, rules,
createdAt, updatedAt }`.

## Tags & canned replies

| Method | Route | Auth | Notes |
| --- | --- | --- | --- |
| POST | `/api/workspaces/{workspaceId}/tags` | Admin | Names lowercased; duplicate → **409** |
| GET | `/api/workspaces/{workspaceId}/tags` | Any | |
| DELETE | `/api/tags/{id}` | Admin | |
| PUT | `/api/conversations/{conversationId}/tags/{tagId}` | Any | Idempotent; foreign tag → **422** |
| DELETE | `/api/conversations/{conversationId}/tags/{tagId}` | Any | |
| POST | `/api/workspaces/{workspaceId}/canned-replies` | Admin | Unique shortcut → **409** |
| GET | `/api/workspaces/{workspaceId}/canned-replies` | Any | `?q=` searches shortcut/title/body |
| GET | `/api/canned-replies/{id}` | Any | |
| PUT | `/api/canned-replies/{id}` | Admin | |
| DELETE | `/api/canned-replies/{id}` | Admin | |

`TagResponse`: `{ id, workspaceId, name, createdAt }`. `CannedReplyResponse`:
`{ id, workspaceId, shortcut, title, body, createdAt }`. Tagging/untagging a
conversation returns **204**.
