# 0006 — No concurrency token on the round-robin pointer

**Status:** Accepted

## Context

Auto-assignment round-robins new conversations across eligible teammates, and
remembers whose turn is next in `Inbox.LastAssignedTeammateId`. Two conversations
can arrive at the same instant, each read the pointer, pick the next teammate,
and write the pointer back — a classic lost update. Harbor already has an
optimistic-concurrency mechanism (`IHasVersion`) used on conversations and
webhook deliveries; the question is whether the rotation pointer should carry one
too.

A token here would make the second writer's `SaveChanges` fail with a
`DbUpdateConcurrencyException` → `409`. But that `SaveChanges` is committing a
*customer's new conversation* along with the pointer bump. Failing it to protect
the pointer means rejecting an incoming customer message.

## Decision

Deliberately give the round-robin pointer **no** concurrency token.

`Inbox.LastAssignedTeammateId` is an ordinary column. A concurrent update to it
is allowed to be lost.

## Consequences

- The worst case of a lost pointer update is that the rotation repeats a
  teammate — one person gets an extra conversation. That imbalance self-corrects
  on the next assignment.
- No customer conversation is ever failed to preserve rotation fairness. Every
  conversation still reaches a real, available teammate; only the *order* can
  wobble under concurrency.
- This is the opposite call from conversations and deliveries, where a lost
  update destroys real work (two agents overwriting each other; the same webhook
  delivered twice) and a token is worth a `409`. The asymmetry is intentional:
  tokens protect correctness, and rotation fairness is not a correctness
  property.
- `ConcurrencyTests` pins both sides — a stale conversation/delivery write is
  refused, and a concurrent pointer update is proven to be silently lost without
  error.
