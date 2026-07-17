using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Infrastructure;

/// <summary>An outbound reply rendered as an email, ready to hand to a relay.</summary>
public record RenderedEmail(
    string MessageId, string From, string To, string Subject,
    string? InReplyTo, IReadOnlyList<string> References, string Body);

/// <summary>
/// Renders teammate replies as emails and derives the threading headers that
/// keep them in the customer's existing mail thread.
/// </summary>
public static class EmailRendering
{
    /// <summary>Host used to mint outbound Message-IDs.</summary>
    public const string MessageIdHost = "harbor.local";

    /// <summary>
    /// The Message-ID for an outbound message, derived from its id so it is
    /// stable: rendering the same reply twice cannot produce two identities,
    /// and an inbound bounce or reply can always be traced back to the row.
    /// </summary>
    public static string MessageIdFor(Guid messageId) => $"<{messageId:D}@{MessageIdHost}>";

    /// <summary>Prefixes "Re: " unless the subject already carries one.</summary>
    public static string ReplySubject(string? subject)
    {
        var trimmed = subject?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return "Re: (no subject)";
        }

        return trimmed.StartsWith("Re:", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"Re: {trimmed}";
    }

    /// <summary>
    /// Renders a teammate reply for the conversation's contact.
    /// </summary>
    /// <param name="thread">
    /// The conversation's messages, in any order. Only messages carrying an
    /// email identity contribute to the References chain; chat messages and
    /// internal notes are invisible to the customer and must never leak into
    /// an outbound header.
    /// </param>
    public static RenderedEmail Render(
        Conversation conversation, Inbox inbox, Contact contact,
        Message message, IEnumerable<Message> thread)
    {
        if (inbox.EmailAddress is not { } from)
        {
            throw new DomainException("The conversation's inbox has no email address.");
        }

        if (contact.Email is not { } to)
        {
            throw new DomainException("The contact has no email address to reply to.");
        }

        if (message.Kind == MessageKind.Note)
        {
            throw new DomainException("Internal notes are never sent to contacts.");
        }

        if (message.AuthorType != AuthorType.Teammate)
        {
            throw new DomainException("Only teammate replies are rendered as outbound email.");
        }

        var priorEmails = thread
            .Where(m => m.Kind == MessageKind.Reply
                && m.EmailMessageId is not null
                && m.CreatedAt < message.CreatedAt)
            .OrderBy(m => m.CreatedAt)
            .ToList();

        // Reply to the most recent thing the customer can actually see.
        var inReplyTo = priorEmails.LastOrDefault()?.EmailMessageId;
        var references = priorEmails.Select(m => m.EmailMessageId!).ToList();

        return new RenderedEmail(
            message.EmailMessageId ?? MessageIdFor(message.Id),
            from,
            to,
            ReplySubject(conversation.Subject),
            inReplyTo,
            references,
            message.Body);
    }
}
