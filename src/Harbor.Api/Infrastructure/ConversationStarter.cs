using Harbor.Api.Contracts;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Api.Infrastructure;

/// <summary>
/// Everything that must happen when a conversation begins, in one place:
/// stamp SLA targets, round-robin auto-assign, record the assignment, and
/// queue the webhooks.
///
/// This exists so every channel a conversation can arrive on gets identical
/// treatment. Chat and inbound email both come through here, which is what
/// stops email conversations from quietly skipping auto-assignment or SLA
/// because a second code path forgot to call something.
/// </summary>
public static class ConversationStarter
{
    /// <summary>
    /// Builds the conversation and its opening contact message, applies the
    /// start-time rules, and queues events. The caller still SaveChanges, so
    /// the conversation, its assignment, and its events commit together.
    /// </summary>
    public static async Task<Conversation> StartAsync(
        HarborDbContext db, Inbox inbox, Contact contact, string? subject, string body,
        DateTimeOffset now, ConversationPriority priority = ConversationPriority.Normal,
        MessageChannel channel = MessageChannel.Chat, string? emailMessageId = null)
    {
        var conversation = new Conversation
        {
            WorkspaceId = inbox.WorkspaceId,
            InboxId = inbox.Id,
            ContactId = contact.Id,
            Subject = subject,
            Priority = priority,
            Channel = channel,
            CreatedAt = now,
            UpdatedAt = now,
            LastMessageAt = now,
        };
        await SlaPolicies.ApplyAsync(db, conversation, inbox);

        conversation.Messages.Add(new Message
        {
            ConversationId = conversation.Id,
            AuthorType = AuthorType.Contact,
            AuthorContactId = contact.Id,
            Body = body,
            Channel = channel,
            EmailMessageId = emailMessageId,
            CreatedAt = now,
        });
        contact.LastSeenAt = now;

        if (inbox.AutoAssign && await AutoAssigner.PickNextAsync(db, inbox) is { } assignee)
        {
            conversation.AssignToTeammate(assignee.Id, now);
            db.AssignmentEvents.Add(new AssignmentEvent
            {
                ConversationId = conversation.Id,
                Kind = AssignmentKind.Auto,
                ToTeammateId = assignee.Id,
                CreatedAt = now,
            });
        }

        db.Conversations.Add(conversation);

        // Queued in the same SaveChanges as the conversation itself, so the
        // event cannot outlive a rolled-back write or be lost after a commit.
        await Webhooks.PublishAsync(
            db, inbox.WorkspaceId, WebhookEventType.ConversationCreated,
            conversation.ToSummaryResponse(), now);
        if (conversation.AssignedTeammateId is not null)
        {
            await Webhooks.PublishAsync(
                db, inbox.WorkspaceId, WebhookEventType.ConversationAssigned,
                conversation.ToSummaryResponse(), now);
        }

        return conversation;
    }
}
