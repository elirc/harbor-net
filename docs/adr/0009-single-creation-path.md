# 0009 — One conversation-creation path (`ConversationStarter`)

**Status:** Accepted

## Context

A conversation can begin on more than one channel. Today it is chat
(`POST /api/workspaces/{id}/conversations`) and inbound email
(`POST /api/workspaces/{id}/email/inbound`); tomorrow it could be another. Every
channel must do the same things at birth: build the opening contact message,
stamp SLA targets from the governing policy, round-robin auto-assign if the inbox
wants it (and record the assignment), and queue the `conversation.created` (and
possibly `conversation.assigned`) webhooks — all committing together.

If each channel's controller assembles a conversation itself, these steps drift.
The classic symptom is an email conversation that quietly skips auto-assignment
or SLA stamping because the email path forgot to call one of them — a bug that is
invisible until someone notices emailed tickets never get assigned.

## Decision

Funnel all creation through one function, `ConversationStarter.StartAsync`.

Both the chat controller and the email controller call it. In one place it builds
the conversation and its opening message, applies SLA targets, auto-assigns and
records the `AssignmentEvent`, and queues the webhooks. The caller performs the
single `SaveChangesAsync`, so the conversation, its assignment event, and its
deliveries commit atomically. **Any new creation path must go through it.**

## Consequences

- Chat, email, and any future channel get identical start-time behavior by
  construction — there is no second place for the steps to diverge.
- The atomic commit means a conversation cannot exist without its start-time
  side effects, or vice versa (the webhook queuing shares the outbox transaction;
  see [ADR-0003](0003-transactional-outbox.md)).
- `ChannelParityTests` runs one scenario down both channels and asserts the
  results are equivalent (same SLA, same assignee, same events), so a divergent
  new path is caught.
- It is a convention, not something the compiler enforces: a future contributor
  *could* write a controller that news up a `Conversation` directly. The
  parity tests and this ADR are the guard rails.
