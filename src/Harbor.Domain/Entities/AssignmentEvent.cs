namespace Harbor.Domain.Entities;

/// <summary>
/// Audit-trail entry for every assignment change on a conversation. Ids are
/// stored raw (no foreign keys) so history survives directory changes.
/// </summary>
public class AssignmentEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public AssignmentKind Kind { get; set; }

    /// <summary>The teammate who made the change; null for auto-assignment.</summary>
    public Guid? ActorTeammateId { get; set; }

    public Guid? FromTeammateId { get; set; }
    public Guid? FromTeamId { get; set; }
    public Guid? ToTeammateId { get; set; }
    public Guid? ToTeamId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
}
