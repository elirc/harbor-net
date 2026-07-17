# harbor-net

An Intercom-style customer-messaging platform — C#/.NET backend only.

Workspaces, inboxes, contacts, teammates, teams, conversations with threaded
messages and internal notes, conversation states (open / snoozed / closed),
assignment, tags, canned replies, first-response SLA tracking, and
search/filter endpoints.

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

In Development the API migrates a local SQLite database (`harbor.db`) and
seeds a demo workspace ("Acme Support") with inboxes, teammates, teams,
contacts, tagged conversations, notes, and one SLA-breached conversation.
OpenAPI is served at `/openapi/v1.json`.

## Domain model

- **Workspace** — top-level tenant; everything else hangs off it.
- **Inbox** — a channel inside a workspace; optionally carries
  `firstResponseSlaMinutes`, which stamps `firstResponseDueAt` on new
  conversations.
- **Contact** — an end-user writing in. **Teammate** — an agent.
  **Team** — a group of teammates.
- **Conversation** — a thread between one contact and the workspace, living
  in one inbox. States: `Open`, `Snoozed` (with a future wake time), `Closed`.
  Assignable to exactly one teammate or team.
- **Message** — `Reply` (visible to the contact) or `Note` (internal).
  The first teammate reply stamps `firstRespondedAt`; a contact reply on a
  snoozed/closed conversation reopens it; notes never change state or SLA.
- **Tag** — workspace-scoped label attached to conversations.
- **Canned reply** — saved response with a unique shortcut per workspace.

## API surface

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/health` | Service health, name, version |
| POST/GET | `/api/workspaces` | Create / list workspaces |
| GET | `/api/workspaces/{id}` | Workspace detail |
| POST/GET | `/api/workspaces/{id}/inboxes` | Create / list inboxes (optional SLA minutes) |
| GET | `/api/workspaces/{id}/inboxes/{inboxId}` | Inbox detail |
| POST/GET | `/api/workspaces/{id}/contacts` | Create / list-search contacts (`?q=`) |
| GET/PUT/DELETE | `/api/contacts/{id}` | Contact detail / update / delete (409 with conversations) |
| POST/GET | `/api/workspaces/{id}/teammates` | Create (unique email) / list teammates |
| GET | `/api/teammates/{id}` | Teammate detail |
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
| POST/GET | `/api/workspaces/{id}/tags` | Create / list tags |
| DELETE | `/api/tags/{id}` | Delete tag (cascades off conversations) |
| PUT/DELETE | `/api/conversations/{id}/tags/{tagId}` | Tag / untag a conversation |
| POST/GET | `/api/workspaces/{id}/canned-replies` | Create / list-search canned replies (`?q=`) |
| GET/PUT/DELETE | `/api/canned-replies/{id}` | Canned reply detail / update / delete |

### Conversation filters

`GET /api/workspaces/{id}/conversations` accepts any combination of:

- `state=Open|Snoozed|Closed`
- `inboxId`, `contactId`, `assignedTeammateId`, `assignedTeamId`
- `unassigned=true` — nobody assigned
- `tag=billing` — exact tag name, case-insensitive
- `q=invoice` — case-insensitive search across subject and reply bodies
  (internal notes are excluded)
- `slaBreached=true` — first response overdue, or delivered after the deadline

Results are ordered by most recent activity.

### Error contract

Errors are RFC 7807 `application/problem+json`: 400 for validation and
malformed input, 404 for unknown resources, 409 for uniqueness conflicts,
422 for domain-rule violations (cross-workspace references, snoozing into
the past, ambiguous assignment, note authored by a contact).

## SQLite & DateTimeOffset

SQLite cannot order or compare `DateTimeOffset` natively. Every
`DateTimeOffset` property is stored as its **UTC tick count** (`long`) via a
`ValueConverter` applied in `ConfigureConventions`, so `ORDER BY` /
comparisons are always chronological regardless of the original offset.
Values round-trip normalized to UTC.

## Tests

```bash
dotnet test
```

96 tests: domain unit tests (state machine, SLA rules, converter semantics,
seeder idempotence) and end-to-end integration tests over every endpoint via
`WebApplicationFactory` + in-memory SQLite.
