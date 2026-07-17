namespace Harbor.Domain.Entities;

/// <summary>
/// A first-response and/or resolution target that applies to conversations
/// matching an optional inbox and priority. A null <see cref="InboxId"/> or
/// <see cref="Priority"/> means "any" — so a policy with both null is the
/// workspace-wide default. The most specific matching policy wins; see
/// <see cref="Specificity"/>.
/// </summary>
public class SlaPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>Null applies the policy to every inbox in the workspace.</summary>
    public Guid? InboxId { get; set; }

    /// <summary>Null applies the policy to every priority.</summary>
    public ConversationPriority? Priority { get; set; }

    /// <summary>Minutes from conversation creation to the first teammate reply.</summary>
    public int? FirstResponseMinutes { get; set; }

    /// <summary>Minutes from conversation creation to the conversation being closed.</summary>
    public int? ResolutionMinutes { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public Inbox? Inbox { get; set; }

    /// <summary>
    /// How narrowly this policy is targeted. Inbox is weighted above priority
    /// so an inbox-specific policy beats a priority-wide one; the resolver
    /// picks the highest score among matching policies.
    /// </summary>
    public int Specificity => (InboxId is null ? 0 : 2) + (Priority is null ? 0 : 1);

    /// <summary>True when this policy's scope covers the given conversation.</summary>
    public bool Matches(Guid inboxId, ConversationPriority priority) =>
        (InboxId is null || InboxId == inboxId)
        && (Priority is null || Priority == priority);
}
