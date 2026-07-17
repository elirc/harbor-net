namespace Harbor.Domain;

public enum SegmentMatch
{
    /// <summary>Every condition must hold.</summary>
    All = 0,

    /// <summary>At least one condition must hold.</summary>
    Any = 1,
}

public enum SegmentOperator
{
    Equals = 0,
    NotEquals = 1,
    Contains = 2,
    NotContains = 3,
    StartsWith = 4,
    EndsWith = 5,

    /// <summary>The field is present and non-null.</summary>
    Exists = 6,

    /// <summary>The field is absent or null.</summary>
    NotExists = 7,

    /// <summary>Date fields only.</summary>
    Before = 8,

    /// <summary>Date fields only.</summary>
    After = 9,
}

/// <summary>
/// One rule. <see cref="Field"/> is either a built-in contact field
/// (name, email, externalId, createdAt, lastSeenAt) or a custom attribute
/// addressed as "attributes.&lt;key&gt;".
/// </summary>
public record SegmentCondition(string Field, SegmentOperator Operator, string? Value = null);

/// <summary>The rules defining a segment, as stored on <see cref="Entities.Segment"/>.</summary>
public record SegmentRuleSet(SegmentMatch Match, IReadOnlyList<SegmentCondition> Conditions);

/// <summary>Field names a segment rule may address.</summary>
public static class SegmentFields
{
    public const string Name = "name";
    public const string Email = "email";
    public const string ExternalId = "externalid";
    public const string CreatedAt = "createdat";
    public const string LastSeenAt = "lastseenat";

    /// <summary>Prefix for custom attributes: "attributes.plan".</summary>
    public const string AttributePrefix = "attributes.";

    public static readonly string[] BuiltIn = [Name, Email, ExternalId, CreatedAt, LastSeenAt];

    public static bool IsDateField(string field) =>
        field is CreatedAt or LastSeenAt;

    /// <summary>The attribute key, or null when the field is not an attribute.</summary>
    public static string? AttributeKey(string field) =>
        field.StartsWith(AttributePrefix, StringComparison.Ordinal)
            ? field[AttributePrefix.Length..]
            : null;
}
