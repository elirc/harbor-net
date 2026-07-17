namespace Harbor.Domain;

/// <summary>
/// Pure summary statistics for reporting. Kept dependency-free so the maths
/// can be unit-tested without a database.
/// </summary>
public static class Statistics
{
    /// <summary>
    /// The value at the given percentile (0-100) using linear interpolation
    /// between closest ranks — the same definition as Excel's PERCENTILE.INC
    /// and NumPy's default. Interpolating is what makes the median of an
    /// even-sized sample the midpoint of the two middle values rather than an
    /// arbitrary one of them. Returns null for an empty sample.
    /// </summary>
    /// <param name="values">Values in any order; a sorted copy is taken.</param>
    public static double? Percentile(IEnumerable<double> values, double percentile)
    {
        if (percentile is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(percentile), percentile, "Percentile must be between 0 and 100.");
        }

        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var rank = percentile / 100d * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = rank - lower;
        return (sorted[lower] * (1 - weight)) + (sorted[upper] * weight);
    }

    /// <summary>The arithmetic mean, or null for an empty sample.</summary>
    public static double? Average(IEnumerable<double> values)
    {
        var list = values as IReadOnlyCollection<double> ?? values.ToList();
        return list.Count == 0 ? null : list.Sum() / list.Count;
    }
}
