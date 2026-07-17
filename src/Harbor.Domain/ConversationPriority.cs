namespace Harbor.Domain;

/// <summary>
/// Urgency of a conversation. Ordered so that higher values are more urgent,
/// which lets reporting and filters compare them meaningfully.
/// </summary>
public enum ConversationPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3,
}
