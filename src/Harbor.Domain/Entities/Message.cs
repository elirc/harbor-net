namespace Harbor.Domain.Entities;

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public MessageKind Kind { get; set; } = MessageKind.Reply;
    public AuthorType AuthorType { get; set; }
    public Guid? AuthorContactId { get; set; }
    public Guid? AuthorTeammateId { get; set; }
    public required string Body { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
    public Contact? AuthorContact { get; set; }
    public Teammate? AuthorTeammate { get; set; }
}
