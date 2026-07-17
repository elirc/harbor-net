namespace Harbor.Domain.Entities;

/// <summary>A saved reply (macro) teammates can insert into conversations.</summary>
public class CannedReply
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }

    /// <summary>Short trigger, e.g. "refund-policy".</summary>
    public required string Shortcut { get; set; }

    public required string Title { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
}
