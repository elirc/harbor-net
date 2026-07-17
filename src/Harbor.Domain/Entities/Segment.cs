namespace Harbor.Domain.Entities;

/// <summary>
/// A dynamic group of contacts, defined by rules rather than membership.
/// Nothing is stored per contact: membership is whatever the rules select at
/// the moment you ask, so a contact joins or leaves the instant their
/// attributes change.
/// </summary>
public class Segment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>The serialized <see cref="SegmentRuleSet"/>.</summary>
    public required string RulesJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
}
