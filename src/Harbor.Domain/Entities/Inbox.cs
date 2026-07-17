namespace Harbor.Domain.Entities;

public class Inbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>
    /// When set, new conversations in this inbox get a first-response SLA of
    /// CreatedAt + this many minutes.
    /// </summary>
    public int? FirstResponseSlaMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
}
