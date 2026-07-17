using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

public class EmailRenderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly Inbox Inbox = new()
    {
        WorkspaceId = Guid.NewGuid(),
        Name = "Support",
        EmailAddress = "support@acme.test",
    };

    private static readonly Contact Contact = new()
    {
        WorkspaceId = Inbox.WorkspaceId,
        Name = "Jane",
        Email = "jane@example.test",
    };

    private static Conversation NewConversation(string? subject = "Cannot log in") => new()
    {
        WorkspaceId = Inbox.WorkspaceId,
        InboxId = Inbox.Id,
        ContactId = Contact.Id,
        Subject = subject,
        Channel = MessageChannel.Email,
        CreatedAt = Now.AddHours(-1),
    };

    private static Message Reply(DateTimeOffset at, string? emailMessageId = null) => new()
    {
        AuthorType = AuthorType.Teammate,
        AuthorTeammateId = Guid.NewGuid(),
        Kind = MessageKind.Reply,
        Body = "Have you tried resetting?",
        Channel = MessageChannel.Email,
        EmailMessageId = emailMessageId,
        CreatedAt = at,
    };

    private static Message Inbound(DateTimeOffset at, string emailMessageId) => new()
    {
        AuthorType = AuthorType.Contact,
        AuthorContactId = Contact.Id,
        Kind = MessageKind.Reply,
        Body = "I cannot log in.",
        Channel = MessageChannel.Email,
        EmailMessageId = emailMessageId,
        CreatedAt = at,
    };

    [Theory]
    [InlineData("Cannot log in", "Re: Cannot log in")]
    [InlineData("Re: Cannot log in", "Re: Cannot log in")]
    [InlineData("RE: Cannot log in", "RE: Cannot log in")]
    [InlineData(null, "Re: (no subject)")]
    [InlineData("   ", "Re: (no subject)")]
    public void ReplySubject_PrefixesOnlyOnce(string? subject, string expected)
    {
        Assert.Equal(expected, EmailRendering.ReplySubject(subject));
    }

    [Fact]
    public void MessageId_IsDerivedFromTheMessage_AndStable()
    {
        var id = Guid.NewGuid();

        Assert.Equal(EmailRendering.MessageIdFor(id), EmailRendering.MessageIdFor(id));
        Assert.Equal($"<{id:D}@harbor.local>", EmailRendering.MessageIdFor(id));
    }

    [Fact]
    public void Render_AddressesTheContactFromTheInbox()
    {
        var conversation = NewConversation();
        var message = Reply(Now);

        var email = EmailRendering.Render(conversation, Inbox, Contact, message, [message]);

        Assert.Equal("support@acme.test", email.From);
        Assert.Equal("jane@example.test", email.To);
        Assert.Equal("Re: Cannot log in", email.Subject);
        Assert.Equal("Have you tried resetting?", email.Body);
        Assert.Equal(EmailRendering.MessageIdFor(message.Id), email.MessageId);
    }

    [Fact]
    public void Render_ThreadsOntoTheLatestPriorEmail()
    {
        var conversation = NewConversation();
        var first = Inbound(Now.AddMinutes(-30), "<first@mail.test>");
        var second = Inbound(Now.AddMinutes(-10), "<second@mail.test>");
        var message = Reply(Now);

        var email = EmailRendering.Render(
            conversation, Inbox, Contact, message, [first, second, message]);

        Assert.Equal("<second@mail.test>", email.InReplyTo);
        Assert.Equal(["<first@mail.test>", "<second@mail.test>"], email.References);
    }

    [Fact]
    public void Render_IgnoresNotesAndChatMessages_InTheReferencesChain()
    {
        var conversation = NewConversation();
        var inbound = Inbound(Now.AddMinutes(-30), "<first@mail.test>");
        var note = new Message
        {
            AuthorType = AuthorType.Teammate,
            Kind = MessageKind.Note,
            Body = "Internal: this customer is a VIP.",
            EmailMessageId = "<leaky@mail.test>",
            CreatedAt = Now.AddMinutes(-20),
        };
        var chat = new Message
        {
            AuthorType = AuthorType.Contact,
            Kind = MessageKind.Reply,
            Body = "Also asked in chat",
            Channel = MessageChannel.Chat,
            CreatedAt = Now.AddMinutes(-15),
        };
        var message = Reply(Now);

        var email = EmailRendering.Render(
            conversation, Inbox, Contact, message, [inbound, note, chat, message]);

        // An internal note must never surface in a header the customer sees.
        Assert.Equal(["<first@mail.test>"], email.References);
        Assert.Equal("<first@mail.test>", email.InReplyTo);
    }

    [Fact]
    public void Render_FirstReplyHasNoInReplyTo()
    {
        var conversation = NewConversation();
        var message = Reply(Now);

        var email = EmailRendering.Render(conversation, Inbox, Contact, message, [message]);

        Assert.Null(email.InReplyTo);
        Assert.Empty(email.References);
    }

    [Fact]
    public void Render_UsesTheStoredMessageId_WhenPresent()
    {
        var conversation = NewConversation();
        var message = Reply(Now, "<stamped@harbor.local>");

        var email = EmailRendering.Render(conversation, Inbox, Contact, message, [message]);

        Assert.Equal("<stamped@harbor.local>", email.MessageId);
    }

    [Fact]
    public void Render_WithoutAnInboxAddress_Throws()
    {
        var inbox = new Inbox { WorkspaceId = Inbox.WorkspaceId, Name = "Chat only" };
        var message = Reply(Now);

        Assert.Throws<DomainException>(
            () => EmailRendering.Render(NewConversation(), inbox, Contact, message, [message]));
    }

    [Fact]
    public void Render_WithoutAContactAddress_Throws()
    {
        var contact = new Contact { WorkspaceId = Inbox.WorkspaceId, Name = "Anonymous" };
        var message = Reply(Now);

        Assert.Throws<DomainException>(
            () => EmailRendering.Render(NewConversation(), Inbox, contact, message, [message]));
    }

    [Fact]
    public void Render_OfANote_Throws()
    {
        var note = new Message
        {
            AuthorType = AuthorType.Teammate,
            Kind = MessageKind.Note,
            Body = "Internal only",
            CreatedAt = Now,
        };

        Assert.Throws<DomainException>(
            () => EmailRendering.Render(NewConversation(), Inbox, Contact, note, [note]));
    }

    [Fact]
    public void Render_OfAContactMessage_Throws()
    {
        var inbound = Inbound(Now, "<theirs@mail.test>");

        Assert.Throws<DomainException>(
            () => EmailRendering.Render(NewConversation(), Inbox, Contact, inbound, [inbound]));
    }
}
