# Getting started

Run Harbor locally, meet the seeded demo data, and walk a conversation from a
customer message to a closed ticket. Every command below was run against the app
on a spare port before this was written; the flow is real, not aspirational.

## Prerequisites

- .NET 10 SDK
- `curl` and (for the pretty-printing in the walkthrough) `python`

## Build, test, run

```bash
dotnet build
dotnet test
dotnet run --project src/Harbor.Api
```

`dotnet run` uses the `http` launch profile and listens on
**`http://localhost:5230`** (the `https` profile adds
`https://localhost:7287`). In the **Development** environment — which the launch
profiles set — startup runs `db.Database.Migrate()` against a local SQLite file
`harbor.db` and then `DataSeeder.Seed`, which populates a demo workspace.
OpenAPI is served at `/openapi/v1.json` (Development only).

> To run against a different database, set the `Harbor` connection string, e.g.
> `ConnectionStrings__Harbor="Data Source=C:/tmp/harbor.db"`. Migration and
> seeding still only happen in Development.

## The seeded demo

The seeder creates one workspace, **Acme Support**, with well-known development
API keys (hashed at rest, like real keys):

| Key | Teammate | Role | Notes |
| --- | --- | --- | --- |
| `hbk_dev_ada_admin` | Ada Lovelace | Admin | |
| `hbk_dev_grace_agent` | Grace Hopper | Agent | capacity 5 |
| `hbk_dev_linus_agent` | Linus Pauling | Agent | Away |

It also seeds two inboxes — **Support** (`support@acme.test`, auto-assign on,
60-minute first-response fallback) and **Sales** (chat-only) — two teams
(Frontline, Billing), three contacts (Mario, Jane, Kenji), three tags
(`billing`, `bug`, `vip`), two canned replies, two SLA policies for Support
(Standard 60/2880 min, Urgent 15/240 min), a "Getting started" collection with
two published articles and one draft, two segments, and four conversations in
assorted states (one open+urgent with a first-response breach, one assigned, one
snoozed, one closed).

Set up a couple of shell variables to follow along:

```bash
B=http://localhost:5230
ADA='X-Api-Key: hbk_dev_ada_admin'
GRACE='X-Api-Key: hbk_dev_grace_agent'
```

Find the workspace id and inboxes:

```bash
WS=$(curl -s -H "$ADA" "$B/api/workspaces" \
  | python -c "import sys,json;print(json.load(sys.stdin)[0]['id'])")

curl -s -H "$ADA" "$B/api/workspaces/$WS/inboxes"
```

## Walkthrough: a conversation, end to end

This is the core support loop — **a customer writes in → auto-assign → an agent
replies → the SLA is satisfied → the conversation is closed** — with real output
shapes.

### 1. Create the contact

```bash
CONTACT=$(curl -s -H "$ADA" -H 'Content-Type: application/json' \
  -d '{"name":"Wanda Maximoff","email":"wanda@example.com"}' \
  "$B/api/workspaces/$WS/contacts" \
  | python -c "import sys,json;print(json.load(sys.stdin)['id'])")
```

(In production the contact is usually created by the first inbound message; here
we create one explicitly so the ids are easy to reuse.)

### 2. The customer starts a conversation

`POST .../conversations` goes through `ConversationStarter`, so SLA stamping,
auto-assignment, and webhooks all fire in one transaction. The Support inbox has
`autoAssign` on, so the conversation comes back already assigned and already
carrying its SLA targets:

```bash
SUPPORT=$(curl -s -H "$ADA" "$B/api/workspaces/$WS/inboxes" \
  | python -c "import sys,json;print([i['id'] for i in json.load(sys.stdin) if i['name']=='Support'][0])")

CONVO=$(curl -s -H "$ADA" -H 'Content-Type: application/json' \
  -d "{\"inboxId\":\"$SUPPORT\",\"contactId\":\"$CONTACT\",\"subject\":\"Billing question\",\"body\":\"Why was I charged twice?\"}" \
  "$B/api/workspaces/$WS/conversations")
echo "$CONVO" | python -m json.tool
```

The response (`ConversationDetailResponse`) shows a non-null
`assignedTeammateId` (the round-robin picked the first available teammate),
a `slaPolicyId`, and a `firstResponseDueAt` exactly 60 minutes after
`createdAt`. Grab the id for the next steps:

```bash
CONVO_ID=$(echo "$CONVO" | python -c "import sys,json;print(json.load(sys.stdin)['id'])")
```

### 3. An agent replies

Acting as Grace (an agent — no admin key needed to work conversations), post a
reply. The first teammate reply stamps `firstRespondedAt`, which is what the SLA
engine checks:

```bash
GRACE_ID=$(curl -s -H "$ADA" "$B/api/workspaces/$WS/teammates" \
  | python -c "import sys,json;print([t['id'] for t in json.load(sys.stdin) if t['name']=='Grace Hopper'][0])")

curl -s -H "$GRACE" -H 'Content-Type: application/json' \
  -d "{\"authorType\":\"Teammate\",\"authorId\":\"$GRACE_ID\",\"kind\":\"Reply\",\"body\":\"Sorry! I see the duplicate charge and have issued a refund.\"}" \
  "$B/api/conversations/$CONVO_ID/messages"
```

Fetch the conversation and confirm `firstRespondedAt` is now set:

```bash
curl -s -H "$ADA" "$B/api/conversations/$CONVO_ID" \
  | python -c "import sys,json;c=json.load(sys.stdin);print('firstRespondedAt=',c['firstRespondedAt'])"
```

### 4. Close it, and confirm no SLA breach

```bash
curl -s -H "$GRACE" -H 'Content-Type: application/json' \
  -d '{"state":"Closed"}' "$B/api/conversations/$CONVO_ID/state"

curl -s -H "$ADA" "$B/api/conversations/$CONVO_ID/sla-breaches"
```

Because the reply landed inside the 60-minute target, the breaches list is
empty (`[]`). Had the reply arrived late, a `FirstResponse` breach would have
been recorded the moment it was posted — and one is recorded on the seeded
urgent conversation, which you can see with:

```bash
CONVO1=$(curl -s -H "$ADA" "$B/api/workspaces/$WS/conversations?priority=Urgent" \
  | python -c "import sys,json;print(json.load(sys.stdin)[0]['id'])")
curl -s -H "$ADA" "$B/api/conversations/$CONVO1/sla-breaches"
```

## Email ingestion

The email channel shares `ConversationStarter`, so an emailed conversation gets
the same SLA, auto-assignment, and webhooks as a chat one. A mail provider posts
already-parsed fields; Harbor does not parse MIME. Route by the `to` address
(the Support inbox listens on `support@acme.test`):

```bash
curl -s -H "$ADA" -H 'Content-Type: application/json' -d '{
  "from": "peter@example.com",
  "fromName": "Peter Parker",
  "to": "support@acme.test",
  "subject": "Cannot upload a photo",
  "body": "The upload button does nothing.",
  "messageId": "<msg-1@example.com>"
}' "$B/api/workspaces/$WS/email/inbound"
```

The response reports `startedNewConversation` and `createdContact` (both `true`
the first time from a new sender). A follow-up email quoting the same
`messageId` in its `inReplyTo` threads onto the same conversation instead of
starting a new one:

```bash
curl -s -H "$ADA" -H 'Content-Type: application/json' -d '{
  "from": "peter@example.com",
  "to": "support@acme.test",
  "subject": "Re: Cannot upload a photo",
  "body": "Still broken on Chrome.",
  "messageId": "<msg-2@example.com>",
  "inReplyTo": "<msg-1@example.com>"
}' "$B/api/workspaces/$WS/email/inbound"
```

An agent's reply on an email conversation is rendered as the outbound email —
with the `In-Reply-To`/`References` headers that keep it in the customer's mail
thread — via `GET /api/messages/{id}/email`.

## Where to go next

- [architecture.md](architecture.md) — how it all fits and why.
- [api-reference.md](api-reference.md) — every endpoint, shape, and status code.
- [adr/](adr/README.md) — the decisions behind the design.
- [testing.md](testing.md) — how the tests are built and run.
