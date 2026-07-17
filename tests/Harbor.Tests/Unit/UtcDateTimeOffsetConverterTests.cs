using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

public class UtcDateTimeOffsetConverterTests
{
    private readonly UtcDateTimeOffsetConverter _converter = new();

    [Fact]
    public void RoundTrips_UtcValue()
    {
        var value = new DateTimeOffset(2026, 7, 16, 12, 30, 45, TimeSpan.Zero);

        var stored = (long)_converter.ConvertToProvider(value)!;
        var restored = (DateTimeOffset)_converter.ConvertFromProvider(stored)!;

        Assert.Equal(value, restored);
        Assert.Equal(TimeSpan.Zero, restored.Offset);
    }

    [Fact]
    public void NormalizesOffsets_ToSameInstant()
    {
        var utc = new DateTimeOffset(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        var plusNine = utc.ToOffset(TimeSpan.FromHours(9));

        var storedUtc = (long)_converter.ConvertToProvider(utc)!;
        var storedPlusNine = (long)_converter.ConvertToProvider(plusNine)!;

        Assert.Equal(storedUtc, storedPlusNine);
    }

    [Fact]
    public void StoredValues_OrderChronologically_AcrossOffsets()
    {
        // Local wall-clock order can disagree with instant order across offsets;
        // stored longs must follow the instant.
        var earlierInstant = new DateTimeOffset(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);
        var laterInstant = new DateTimeOffset(2026, 7, 17, 6, 0, 0, TimeSpan.FromHours(9));

        var storedEarlier = (long)_converter.ConvertToProvider(earlierInstant)!;
        var storedLater = (long)_converter.ConvertToProvider(laterInstant)!;

        Assert.True(storedEarlier < storedLater);
    }
}
