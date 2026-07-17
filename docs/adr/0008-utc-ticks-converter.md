# 0008 — Store `DateTimeOffset` as UTC ticks

**Status:** Accepted

## Context

Harbor uses `DateTimeOffset` throughout — created/updated timestamps, SLA
targets, snooze wake times, message times. SQLite, the backing store, has no
native `DateTimeOffset` type. The provider's default is to store it as a
formatted string, which does **not** sort or compare chronologically once
offsets differ: `2026-01-01T23:00:00+09:00` (14:00 UTC) sorts *after*
`2026-01-01T16:00:00+00:00` as text, even though it is earlier in real time.
Since SLA breach detection, "most recent activity" ordering, and report windows
all depend on `ORDER BY` and range comparisons over these columns, wrong
ordering is a correctness bug, not a cosmetic one.

## Decision

Store every `DateTimeOffset` as its **UTC tick count** (a `long`) via
`UtcDateTimeOffsetConverter`, applied globally as a convention in
`HarborDbContext.ConfigureConventions`:

```csharp
configurationBuilder.Properties<DateTimeOffset>()
    .HaveConversion<UtcDateTimeOffsetConverter>();
```

The converter writes `value.ToUniversalTime().Ticks` and reads back a
`DateTimeOffset` in UTC.

## Consequences

- `ORDER BY` and comparisons are always chronological, regardless of the
  original offset, because every value is a single monotonic integer of the same
  reference frame.
- Values round-trip **normalized to UTC** — the original offset is not
  preserved. That is intentional: Harbor reasons about instants, not wall-clock
  offsets. Callers that need a local rendering apply it at the edge.
- Applying it as a *convention* rather than a per-property mapping means no
  entity has to remember to opt in, and a newly added `DateTimeOffset` property
  is covered automatically.
- `UtcDateTimeOffsetConverterTests` and `HarborDbContextTests` lock in the
  round-trip and the cross-offset ordering so a provider change cannot silently
  regress it.
