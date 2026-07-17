namespace Harbor.Domain.Entities;

/// <summary>An end-user who writes in to a workspace.</summary>
public class Contact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }

    /// <summary>Optional identifier in the customer's own system.</summary>
    public string? ExternalId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public Workspace? Workspace { get; set; }
}
