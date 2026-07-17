namespace Harbor.Domain.Entities;

public class ConversationTag
{
    public Guid ConversationId { get; set; }
    public Guid TagId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
    public Tag? Tag { get; set; }
}
