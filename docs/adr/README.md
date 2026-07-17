# Architecture Decision Records

Short records of the load-bearing decisions in Harbor — the ones where a
reasonable alternative exists and the reason for choosing this one is worth
keeping. Each is written as Context / Decision / Consequences. They describe
decisions already implemented in the codebase (reverse-engineered from it), not
proposals.

| # | Decision |
| --- | --- |
| [0001](0001-api-key-auth.md) | API-key authentication with hashed keys |
| [0002](0002-403-vs-404.md) | 403 for a foreign workspace, 404 for a foreign resource |
| [0003](0003-transactional-outbox.md) | Webhooks via a transactional outbox |
| [0004](0004-timestamp-body-signing.md) | Sign `{timestamp}.{body}`, not the body alone |
| [0005](0005-segments-as-sql.md) | Compile segment rules to SQL |
| [0006](0006-no-token-round-robin.md) | No concurrency token on the round-robin pointer |
| [0007](0007-header-pagination.md) | Pagination in query params and response headers |
| [0008](0008-utc-ticks-converter.md) | Store `DateTimeOffset` as UTC ticks |
| [0009](0009-single-creation-path.md) | One conversation-creation path (`ConversationStarter`) |
