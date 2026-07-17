# Testing

Harbor has **644 tests** (xUnit) in one project, `tests/Harbor.Tests`. They fall
into two families and one high-value guard.

```bash
dotnet test                 # everything
dotnet test --filter "FullyQualifiedName~Unit"          # domain/unit only
dotnet test --filter "FullyQualifiedName~MigrationDrift" # the drift guard
```

## Taxonomy

### Unit tests (`tests/Harbor.Tests/Unit`)

Pure logic, no web host, mostly no database. They exercise the parts that are
easiest to get subtly wrong and hardest to observe from the outside:

- **`ConversationTests`** — state transitions, first-response stamping, note
  inertness, breach predicates.
- **`SlaBoundaryTests`** — breach exactly at the threshold (strict `>`), one
  tick either side, first-response vs resolution judged independently, snooze /
  reopen not moving the clock. These pin arithmetic the wall clock can never
  land on precisely.
- **`SlaPolicyResolutionTests`** — specificity ranking and tie-breaking, targets
  measured from creation.
- **`WebhookSignerTests` / `HttpWebhookSenderTests`** — the HMAC algorithm, and
  the real HTTP sender applying it to the exact bytes so a receiver following the
  documented recipe can verify (including replay rejection).
- **`SegmentCompilerTests`** — rule validation and compilation errors.
- **`UtcDateTimeOffsetConverterTests` / `HarborDbContextTests`** — tick-count
  round-tripping, chronological ordering across offsets, seeder idempotence.
- **`StatisticsTests`, `EmailRenderingTests`, `ArticleSuggestionTests`,
  `ApiKeysTests`, `WebhookDeliveryTests`** — percentile maths, outbound email
  rendering, keyword ranking, key hashing, backoff scheduling.
- **`MigrationDriftTests`** — the schema guard (below).

### Integration tests (`tests/Harbor.Tests/Integration`)

Full HTTP round-trips through `WebApplicationFactory` against an in-memory
SQLite database. `ApiTestBase` provides typed helpers (`CreateWorkspaceAsync`,
`StartConversationAsync`, `BackdateConversation`, …) and the `ActAs` /
`ActAsAdminOf` API-key switching. Highlights:

- **`ConversationStateMachineTests`** — the full transition matrix with exact
  ProblemDetails on rejected moves; contact-reply auto-reopen from every state;
  notes inert to state and SLA.
- **`SlaEngineTests` / `SlaEngineEdgeCaseTests`** — priority-specific policies,
  escalation putting a conversation instantly past due, responded-late vs
  still-overdue, inbox fallback.
- **`AssignmentRulesTests` / `AssignmentEdgeCaseTests`** — round-robin fairness
  over a full rotation, availability and capacity edges, the audit trail, the
  teammate-XOR-team invariant.
- **`WebhookTests` / `WebhookOutboxTests`** — subscriptions, dispatch, retry
  classification, and the outbox invariant (no event without its change via a
  rolled-back transaction; no committed change without its event).
- **`SegmentTests` / `SegmentSqlTranslationTests`** — every operator against
  real rows, plus `ToQueryString` assertions that rules reach the database as a
  `WHERE`/`json_extract` clause rather than in-memory filtering.
- **`EmailChannelTests` / `ChannelParityTests`** — ingestion, threading,
  rendering, and email-vs-chat parity through `ConversationStarter`.
- **`AuthorizationTests` / `AuthorizationMatrixTests` / `WorkspaceScopeMatrixTests`**
  — a parameterized role × workspace × endpoint sweep, and the exact 403-vs-404
  semantics.
- **`ConcurrencyTests`** — stale-write 409s on conversations and deliveries, and
  the deliberate no-token round-robin proven to lose an update silently.
- **`PaginationBoundaryTests`** — the `paging`-binding regression guarded across
  every list endpoint, clamping, malformed input, empty results.
- **`ProductionReadinessTests`, `ReportingTests`, `HelpCenterTests`,
  `ErrorHandlingTests`, `SeedDataSmokeTests`, …** — the remaining surface.

## Harness design

`HarborApiFactory` (a `WebApplicationFactory<Program>`) hosts the API against a
**shared in-memory SQLite** connection that stays open for the factory's
lifetime, so the schema survives between requests. It:

- swaps the real `DbContext` registration for the in-memory connection;
- replaces `IWebhookSender` with `FakeWebhookSender`, which records what would
  have been sent and lets a test choose each attempt's outcome (`Ok`,
  `Rejected`, `Unreachable`) — so retry and dispatch logic can be driven without
  a live endpoint;
- exposes `WithDb(...)` for arrange/assert directly against the database.

Because each test **class** gets its own `HarborApiFactory` (`IClassFixture`),
classes run in parallel while tests within a class share one database. Large
parameterized sweeps are split across classes (e.g. the authorization matrix
into `AuthorizationMatrixTests` + `WorkspaceScopeMatrixTests`) so they run as
parallel collections rather than one long serial queue.

Time-dependent behavior is driven deterministically rather than by waiting:
`BackdateConversation` rewinds a conversation's whole clock so SLA breach paths
fire without a real delay, and `SetConversationTimings` pins lifecycle
timestamps so reports have exact, assertable durations.

## The EnsureCreated-vs-Migrate caveat, and the drift guard

There is a deliberate asymmetry in how the schema is built:

- **Production** (`Program.cs`) runs `db.Database.Migrate()` — it replays the
  migration history.
- **The test harness** (`HarborApiFactory`) runs `db.Database.EnsureCreated()` —
  it materializes the schema straight from the current EF model.

These can silently disagree. If you change the model but forget to add a
migration, `EnsureCreated()` still builds the new shape, so **the whole suite
stays green** — while the real app, running `Migrate()` against an existing
`harbor.db`, ends up with a schema missing the column the entity now expects.
This exact gap bit once in Sprint 08.

**`MigrationDriftTests`** closes it, and is the highest-value test in the suite:

- `Model_HasNoPendingChanges_MissingFromMigrations` diffs the last migration's
  model snapshot against the current model and fails on any pending operation,
  naming it (e.g. `AddColumn Tags.DriftProbe`).
- `Migrations_BuildTheSchema_TheModelExpects` builds a database by actually
  running the migrations and confirms every entity is queryable against it.
- `Seeder_Runs_AgainstAMigratedDatabase` runs `DataSeeder.Seed` on a migrated
  database — exactly what `Program.cs` does after `Migrate()`.
- `EveryMigration_Applies_AndNothingRemainsPending` confirms the history is
  linear and complete.

The guard was validated by falsification: adding an unmapped property to an
entity made it fail naming the exact missing operation, while the rest of the
suite stayed green — precisely the blind spot it closes. **When you change the
model, add a migration:**

```bash
dotnet ef migrations add <Name> \
  --project src/Harbor.Infrastructure \
  --startup-project src/Harbor.Api
```

## Running and flakiness

`dotnet test` runs the whole suite in roughly a minute on a warm build. The
suite is designed to be deterministic — no `Thread.Sleep`, no reliance on wall
clock races — and is run twice on every change to confirm it. If a test ever
fails intermittently, that is a bug in the test (usually shared state or an
order-dependent assertion), not something to retry away.
