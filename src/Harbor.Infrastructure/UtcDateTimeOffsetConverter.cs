using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Harbor.Infrastructure;

/// <summary>
/// SQLite cannot order or compare DateTimeOffset values natively, so every
/// DateTimeOffset is stored as its UTC tick count (a long). Ordering by the
/// column is then always chronological regardless of the original offset.
/// Values round-trip as UTC (offset 00:00).
/// </summary>
public sealed class UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcDateTimeOffsetConverter()
        : base(
            value => value.UtcTicks,
            ticks => new DateTimeOffset(ticks, TimeSpan.Zero))
    {
    }
}
