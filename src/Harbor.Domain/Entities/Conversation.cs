namespace Harbor.Domain.Entities;

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid InboxId { get; set; }
    public Guid ContactId { get; set; }
    public string? Subject { get; set; }

    public ConversationState State { get; set; } = ConversationState.Open;
    public DateTimeOffset? SnoozedUntil { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? AssignedTeammateId { get; set; }
    public Guid? AssignedTeamId { get; set; }

    // Simple SLA fields.
    public DateTimeOffset? FirstResponseDueAt { get; set; }
    public DateTimeOffset? FirstRespondedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public Inbox? Inbox { get; set; }
    public Contact? Contact { get; set; }
    public Teammate? AssignedTeammate { get; set; }
    public Team? AssignedTeam { get; set; }
    public ICollection<Message> Messages { get; } = new List<Message>();
    public ICollection<ConversationTag> Tags { get; } = new List<ConversationTag>();

    // --- Domain behavior -------------------------------------------------

    public void Open(DateTimeOffset now)
    {
        State = ConversationState.Open;
        SnoozedUntil = null;
        ClosedAt = null;
        Touch(now);
    }

    public void Snooze(DateTimeOffset until, DateTimeOffset now)
    {
        if (until <= now)
        {
            throw new DomainException("Snooze time must be in the future.");
        }

        State = ConversationState.Snoozed;
        SnoozedUntil = until;
        ClosedAt = null;
        Touch(now);
    }

    public void Close(DateTimeOffset now)
    {
        State = ConversationState.Closed;
        SnoozedUntil = null;
        ClosedAt = now;
        Touch(now);
    }

    public void AssignToTeammate(Guid teammateId, DateTimeOffset now)
    {
        AssignedTeammateId = teammateId;
        AssignedTeamId = null;
        Touch(now);
    }

    public void AssignToTeam(Guid teamId, DateTimeOffset now)
    {
        AssignedTeamId = teamId;
        AssignedTeammateId = null;
        Touch(now);
    }

    public void Unassign(DateTimeOffset now)
    {
        AssignedTeammateId = null;
        AssignedTeamId = null;
        Touch(now);
    }

    /// <summary>
    /// Registers a message being added to the thread: bumps activity
    /// timestamps, reopens on new contact messages, and records the first
    /// teammate reply for SLA purposes. Notes never affect state or SLA.
    /// </summary>
    public void RegisterMessage(Message message, DateTimeOffset now)
    {
        LastMessageAt = now;

        if (message.Kind == MessageKind.Note)
        {
            Touch(now);
            return;
        }

        if (message.AuthorType == AuthorType.Contact && State != ConversationState.Open)
        {
            Open(now);
            return;
        }

        if (message.AuthorType == AuthorType.Teammate && FirstRespondedAt is null)
        {
            FirstRespondedAt = now;
        }

        Touch(now);
    }

    /// <summary>True when the first-response SLA elapsed with no teammate reply.</summary>
    public bool IsSlaBreached(DateTimeOffset now) =>
        FirstResponseDueAt is { } due
        && (FirstRespondedAt is null ? now > due : FirstRespondedAt > due);

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
