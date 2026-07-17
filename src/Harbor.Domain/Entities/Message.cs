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

    /// <summary>Which channel carried this message. Internal notes are always Chat.</summary>
    public MessageChannel Channel { get; set; } = MessageChannel.Chat;

    /// <summary>
    /// The RFC 5322 Message-ID of the email this message came from or was sent
    /// as. Threading hangs off this: an inbound In-Reply-To/References naming a
    /// known id joins that conversation instead of starting a new one.
    /// </summary>
    public string? EmailMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Conversation? Conversation { get; set; }
    public Contact? AuthorContact { get; set; }
    public Teammate? AuthorTeammate { get; set; }
}
