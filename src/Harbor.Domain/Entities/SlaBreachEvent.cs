namespace Harbor.Domain.Entities;

/// <summary>
/// Records that a conversation missed an SLA target. At most one event per
/// conversation per <see cref="SlaBreachKind"/> (enforced by a unique index),
/// so detection can run repeatedly without duplicating history.
/// </summary>
public class SlaBreachEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public SlaBreachKind Kind { get; set; }

    /// <summary>The target that was missed.</summary>
    public DateTimeOffset DueAt { get; set; }

    /// <summary>
    /// When the breach became true: the late reply/close time, or the moment
    /// detection noticed an overdue conversation that still has neither.
    /// </summary>
    public DateTimeOffset BreachedAt { get; set; }

    /// <summary>The policy in force when the target was set; null for inbox-level SLA.</summary>
    public Guid? SlaPolicyId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
}
