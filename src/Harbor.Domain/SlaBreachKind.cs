namespace Harbor.Domain;

public enum SlaBreachKind
{
    /// <summary>No teammate replied before the first-response target.</summary>
    FirstResponse = 0,

    /// <summary>The conversation was not closed before the resolution target.</summary>
    Resolution = 1,
}
