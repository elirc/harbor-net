# 0004 — Sign `{timestamp}.{body}`, not the body alone

**Status:** Accepted

## Context

Webhook payloads are signed so a receiver can verify they came from Harbor and
were not tampered with. The obvious approach is to HMAC the request body and send
the signature in a header. But a signature over the body alone is replayable
forever: anyone who captures one valid request can resend those exact bytes with
that exact signature indefinitely, and the receiver has no way to tell it is old.

## Decision

Sign the string `"{timestamp}.{body}"` with HMAC-SHA256, and send the timestamp
alongside the signature (`WebhookSigner`):

```
X-Harbor-Signature: t=1784289115,v1=<hex hmac of "1784289115.{body}">
```

The receiver recomputes the HMAC over `"{t}.{body}"` using the shared secret,
compares in constant time, **and** rejects the request if `t` is outside its
tolerance window. `WebhookSigner.TryVerify` implements exactly this so Harbor's
own tests and a real receiver share one definition of correct.

## Consequences

- A captured request cannot be replayed once it is older than the receiver's
  tolerance: the signature is still cryptographically valid, but the timestamp it
  covers is stale, and the attacker cannot re-stamp it without the secret.
  `HttpWebhookSenderTests` demonstrates a capture failing verification after the
  window and passing inside it.
- The signed bytes must be reproducible independently of how the API renders
  responses, so the payload is serialized once at publish time with pinned
  options (`Webhooks.PayloadJson`) and sent verbatim — never re-serialized at
  send time, which would change bytes and invalidate the signature. This is the
  same reason the outbox stores the exact payload
  (see [ADR-0003](0003-transactional-outbox.md)).
- Receivers must implement the two-part check (signature **and** timestamp). The
  header format is Stripe-style, so the recipe is familiar.
- The signing secret is returned only once, at subscription creation, and never
  appears in any response thereafter.
