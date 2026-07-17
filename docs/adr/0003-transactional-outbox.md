# 0003 — Webhooks via a transactional outbox

**Status:** Accepted

## Context

Subscribers want to hear about domain events — `conversation.created`,
`conversation.assigned`, `conversation.closed`, `message.created`. The naive
implementation POSTs to the subscriber's URL inline, inside the request that
caused the event. That has two failure modes that are silent and serious:

1. **Lost events.** If the process commits the change and then dies before the
   HTTP call — or the call fails and is not retried — the change happened but no
   one was told. The event log and reality diverge.
2. **Phantom events.** If the HTTP call fires but the surrounding transaction
   then rolls back, a subscriber was told about a change that never happened.

Inline delivery also couples the caller's latency to the slowest subscriber.

## Decision

Use a transactional outbox.

- `Webhooks.PublishAsync` does **not** send anything. It queues `WebhookDelivery`
  rows into the EF change tracker. The caller's single `SaveChangesAsync` commits
  those rows in the **same transaction** as the change that produced them.
- Delivery is a separate, later step. `WebhookDispatcher.DispatchAsync` (invoked
  by `POST /api/workspaces/{id}/webhooks/dispatch`, which a scheduler calls on a
  timer) reads pending deliveries whose `NextAttemptAt` has arrived, sends them,
  and records the outcome — retrying failures with exponential backoff up to a
  fixed attempt limit.

## Consequences

- The defining invariant holds: **an event exists exactly when its change
  exists.** A rolled-back write takes its queued deliveries with it; a committed
  write cannot have dropped its event. `WebhookOutboxTests` proves both halves,
  including via a deliberately rolled-back transaction.
- Request latency is decoupled from subscribers — publishing is a few in-memory
  inserts.
- Delivery is at-least-once, so subscribers must be idempotent; each delivery
  carries a unique `X-Harbor-Delivery` id for de-duplication. The delivery row
  itself carries a concurrency token so two dispatchers cannot both send it (see
  [ADR-0006](0006-no-token-round-robin.md) for where tokens are and are not used).
- A dispatch trigger must run for events to actually go out; nothing is sent
  until the outbox is drained.
