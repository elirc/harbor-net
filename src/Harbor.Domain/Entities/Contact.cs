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

    /// <summary>
    /// Custom attributes as a JSON object, e.g. {"plan":"enterprise"}. Held as
    /// raw JSON rather than a dictionary so segment rules can be evaluated by
    /// the database (via json_extract) instead of pulling every contact into
    /// memory to filter them.
    /// </summary>
    public string AttributesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }

    public Workspace? Workspace { get; set; }
}
