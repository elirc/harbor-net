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

    /// <summary>
    /// The address inbound email is delivered to, and the From on outbound
    /// replies. Unique across the workspace; null means the inbox is chat-only.
    /// </summary>
    public string? EmailAddress { get; set; }

    /// <summary>When true, new conversations are round-robin assigned to available teammates.</summary>
    public bool AutoAssign { get; set; }

    /// <summary>Round-robin pointer: the teammate who received the last auto-assignment.</summary>
    public Guid? LastAssignedTeammateId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
}
