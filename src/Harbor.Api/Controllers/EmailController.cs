using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

/// <summary>
/// The email channel: ingestion of parsed inbound mail, and rendering of
/// outbound replies.
///
/// Harbor does not parse MIME. A mail provider does that and posts the
/// extracted fields here, authenticating with an API key like any other
/// client — which keeps ingestion inside the same workspace-scoped
/// authorization as everything else.
/// </summary>
[ApiController]
public class EmailController(HarborDbContext db) : ControllerBase
{
    /// <summary>
    /// Ingests one inbound email: routes it to an inbox by its To address,
    /// resolves the sender to a contact, and either threads it onto an
    /// existing conversation or starts a new one.
    /// </summary>
    [HttpPost("api/workspaces/{workspaceId:guid}/email/inbound")]
    public async Task<ActionResult<InboundEmailResponse>> Inbound(
        Guid workspaceId, InboundEmailRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var to = Normalize(request.To);
        var inbox = await db.Inboxes
            .SingleOrDefaultAsync(i => i.WorkspaceId == workspaceId && i.EmailAddress == to);
        if (inbox is null)
        {
            return Problem422(
                "Unknown inbox address",
                $"No inbox in this workspace receives mail at '{to}'.");
        }

        var now = DateTimeOffset.UtcNow;
        var from = Normalize(request.From);

        // The sender may be writing in for the first time; an email address is
        // the only identity an inbound mail carries.
        var contact = await db.Contacts
            .FirstOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Email == from);
        var createdContact = contact is null;
        if (contact is null)
        {
            contact = new Contact
            {
                WorkspaceId = workspaceId,
                Name = string.IsNullOrWhiteSpace(request.FromName) ? from : request.FromName.Trim(),
                Email = from,
                CreatedAt = now,
            };
            db.Contacts.Add(contact);
        }

        var existing = await FindThreadAsync(workspaceId, request);
        if (existing is not null)
        {
            var reply = new Message
            {
                ConversationId = existing.Id,
                Kind = MessageKind.Reply,
                AuthorType = AuthorType.Contact,
                AuthorContactId = existing.ContactId,
                Body = request.Body,
                Channel = MessageChannel.Email,
                EmailMessageId = request.MessageId,
                CreatedAt = now,
            };

            // Goes through the same domain path as a chat reply, so an emailed
            // reply reopens a closed conversation exactly like any other.
            existing.RegisterMessage(reply, now);
            db.Messages.Add(reply);
            contact.LastSeenAt = now;

            await SlaBreaches.DetectAsync(db, existing, now);
            await Webhooks.PublishAsync(
                db, workspaceId, WebhookEventType.MessageCreated, reply.ToResponse(), now);
            await db.SaveChangesAsync();

            return new InboundEmailResponse(existing.Id, reply.Id, contact.Id, false, createdContact);
        }

        var conversation = await ConversationStarter.StartAsync(
            db, inbox, contact, request.Subject, request.Body, now,
            channel: MessageChannel.Email, emailMessageId: request.MessageId);
        await db.SaveChangesAsync();

        return new InboundEmailResponse(
            conversation.Id, conversation.Messages.Single().Id, contact.Id, true, createdContact);
    }

    /// <summary>
    /// Renders a teammate reply as the email that would be sent to the
    /// contact, including the headers that keep it in their mail thread.
    /// </summary>
    [HttpGet("api/messages/{id:guid}/email")]
    public async Task<ActionResult<RenderedEmailResponse>> Render(Guid id)
    {
        var message = await db.Messages
            .Include(m => m.Conversation)
            .SingleOrDefaultAsync(m => m.Id == id
                && m.Conversation!.WorkspaceId == User.GetWorkspaceId());
        if (message?.Conversation is not { } conversation)
        {
            return NotFound();
        }

        var inbox = await db.Inboxes.SingleAsync(i => i.Id == conversation.InboxId);
        var contact = await db.Contacts.SingleAsync(c => c.Id == conversation.ContactId);
        var thread = await db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .ToListAsync();

        // DomainException (no inbox address, no contact address, a note, or a
        // contact's own message) becomes a 422 via DomainExceptionHandler.
        var rendered = EmailRendering.Render(conversation, inbox, contact, message, thread);
        return rendered.ToResponse(message.Id);
    }

    /// <summary>
    /// Finds the conversation an inbound mail belongs to by matching its
    /// In-Reply-To or References against Message-IDs we have already seen.
    /// References is walked newest-first: the nearest known ancestor is the
    /// best evidence of which thread this is.
    /// </summary>
    private async Task<Conversation?> FindThreadAsync(Guid workspaceId, InboundEmailRequest request)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.InReplyTo))
        {
            candidates.Add(request.InReplyTo.Trim());
        }

        if (request.References is { Count: > 0 } references)
        {
            candidates.AddRange(references
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r.Trim())
                .Reverse());
        }

        foreach (var candidate in candidates.Distinct())
        {
            var match = await db.Messages
                .Include(m => m.Conversation)
                .FirstOrDefaultAsync(m => m.EmailMessageId == candidate
                    && m.Conversation!.WorkspaceId == workspaceId);
            if (match?.Conversation is not null)
            {
                return match.Conversation;
            }
        }

        return null;
    }

    /// <summary>Addresses are matched case-insensitively.</summary>
    private static string Normalize(string address) => address.Trim().ToLowerInvariant();

    private ObjectResult Problem422(string title, string detail) =>
        UnprocessableEntity(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status422UnprocessableEntity,
        });
}
