namespace Harbor.Domain.Entities;

public class Conversation : IHasVersion
{
    /// <summary>
    /// Optimistic-concurrency token. Conversations are the one thing several
    /// agents genuinely fight over — two people grabbing or closing the same
    /// one — so a lost update here loses real work.
    /// </summary>
    public Guid Version { get; set; } = Guid.NewGuid();

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

    public ConversationPriority Priority { get; set; } = ConversationPriority.Normal;

    /// <summary>The channel this conversation started on.</summary>
    public MessageChannel Channel { get; set; } = MessageChannel.Chat;

    // SLA targets, stamped from the SLA policy in force (or the inbox's
    // first-response minutes when no policy matches) and re-stamped when
    // priority changes. The clock always runs from CreatedAt.
    public DateTimeOffset? FirstResponseDueAt { get; set; }
    public DateTimeOffset? FirstRespondedAt { get; set; }
    public DateTimeOffset? ResolutionDueAt { get; set; }

    /// <summary>
    /// When the conversation was first closed. Unlike <see cref="ClosedAt"/>
    /// this is never cleared by reopening, so the resolution SLA is judged on
    /// the first resolution — mirroring <see cref="FirstRespondedAt"/>.
    /// </summary>
    public DateTimeOffset? FirstResolvedAt { get; set; }

    /// <summary>The policy whose targets are stamped above; null for inbox-level SLA.</summary>
    public Guid? SlaPolicyId { get; set; }

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
        FirstResolvedAt ??= now;
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

    /// <summary>Sets priority; callers re-stamp SLA targets afterwards.</summary>
    public void SetPriority(ConversationPriority priority, DateTimeOffset now)
    {
        Priority = priority;
        Touch(now);
    }

    /// <summary>
    /// Applies SLA targets measured from <see cref="CreatedAt"/>, so changing
    /// priority mid-conversation moves the deadline rather than restarting the
    /// clock. Null minutes clear the corresponding target.
    /// </summary>
    public void ApplySlaTargets(int? firstResponseMinutes, int? resolutionMinutes, Guid? policyId)
    {
        FirstResponseDueAt = firstResponseMinutes is { } fr ? CreatedAt.AddMinutes(fr) : null;
        ResolutionDueAt = resolutionMinutes is { } res ? CreatedAt.AddMinutes(res) : null;
        SlaPolicyId = policyId;
    }

    /// <summary>True when the first-response SLA elapsed with no teammate reply.</summary>
    public bool IsFirstResponseBreached(DateTimeOffset now) =>
        FirstResponseDueAt is { } due
        && (FirstRespondedAt is null ? now > due : FirstRespondedAt > due);

    /// <summary>True when the resolution SLA elapsed before the first close.</summary>
    public bool IsResolutionBreached(DateTimeOffset now) =>
        ResolutionDueAt is { } due
        && (FirstResolvedAt is null ? now > due : FirstResolvedAt > due);

    /// <summary>True when either SLA target was missed.</summary>
    public bool IsSlaBreached(DateTimeOffset now) =>
        IsFirstResponseBreached(now) || IsResolutionBreached(now);

    private void Touch(DateTimeOffset now) => UpdatedAt = now;
}
