using Harbor.Domain;

namespace Harbor.Tests.Unit;

public class StatisticsTests
{
    [Fact]
    public void Percentile_EmptySample_IsNull()
    {
        Assert.Null(Statistics.Percentile([], 50));
        Assert.Null(Statistics.Average([]));
    }

    [Fact]
    public void Percentile_SingleValue_IsThatValue()
    {
        Assert.Equal(7, Statistics.Percentile([7], 50));
        Assert.Equal(7, Statistics.Percentile([7], 95));
    }

    [Fact]
    public void Median_OfEvenSample_InterpolatesTheMiddlePair()
    {
        Assert.Equal(2.5, Statistics.Percentile([1, 2, 3, 4], 50));
    }

    [Fact]
    public void Median_OfOddSample_IsTheMiddleValue()
    {
        Assert.Equal(3, Statistics.Percentile([1, 2, 3, 4, 5], 50));
    }

    [Fact]
    public void Percentile_SortsInput()
    {
        Assert.Equal(3, Statistics.Percentile([5, 1, 4, 2, 3], 50));
    }

    [Fact]
    public void Percentile_MinAndMax_AreTheExtremes()
    {
        Assert.Equal(1, Statistics.Percentile([1, 2, 3, 4], 0));
        Assert.Equal(4, Statistics.Percentile([1, 2, 3, 4], 100));
    }

    [Fact]
    public void Percentile_MatchesLinearInterpolationDefinition()
    {
        // rank = 0.9 * (10 - 1) = 8.1 -> between 9 and 10, weighted 0.1.
        double[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

        Assert.Equal(9.1, Statistics.Percentile(values, 90)!.Value, 10);
        Assert.Equal(9.55, Statistics.Percentile(values, 95)!.Value, 10);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percentile_OutOfRange_Throws(double percentile)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Statistics.Percentile([1, 2, 3], percentile));
    }

    [Fact]
    public void Average_IsArithmeticMean()
    {
        Assert.Equal(2.5, Statistics.Average([1, 2, 3, 4]));
    }
}
