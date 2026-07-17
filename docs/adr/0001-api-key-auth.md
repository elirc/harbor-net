# 0001 — API-key authentication with hashed keys

**Status:** Accepted

## Context

Harbor is a backend consumed by other systems — a web app, a mobile client, a
mail relay posting inbound email, a scheduler draining webhooks. The callers are
services and long-lived integrations, not interactive users typing a password.
It needs an authentication scheme that is trivial for a service to send on every
request, that ties a request to a specific teammate (for authorization and audit),
and that does not become a liability if the database leaks.

## Decision

Authenticate with a per-teammate API key sent in the `X-Api-Key` header.

- Keys are generated once (`ApiKeys.Generate`, `hbk_` + 48 hex chars), returned
  exactly once at creation, and **only their SHA-256 digest is stored**
  (`Teammate.ApiKeyHash`, a unique column).
- `ApiKeyAuthenticationHandler` hashes the presented key and looks up the
  teammate by digest. The resulting principal carries the teammate id, workspace
  id, and role as claims, which everything downstream (the scope filter, role
  checks, request logging) reads.
- Workspace bootstrap (`POST /api/workspaces`) is anonymous and mints the first
  admin's key — the one path that must work before any key exists.
- A missing or unknown key is `401`; keys are never echoed back in any response
  (`TeammateResponse` has no key field).

## Consequences

- A database disclosure exposes only digests, not usable keys — a plain
  credential can never be replayed from the stored form.
- Every authenticated request is attributable to a teammate, so the assignment
  audit trail and request logs can name the actor without extra plumbing.
- There is no built-in key rotation or expiry; revoking a key means deleting or
  re-issuing the teammate's credential. Acceptable for the current scope, and
  the single lookup point makes adding rotation later a local change.
- Bearer-token / OAuth flows were rejected as overkill for service-to-service
  traffic with no interactive login and no third-party delegation.
