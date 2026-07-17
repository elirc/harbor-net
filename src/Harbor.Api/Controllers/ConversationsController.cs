using Harbor.Api.Contracts;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
public class ConversationsController(HarborDbContext db) : ControllerBase
{
    /// <summary>Starts a conversation with the contact's opening message.</summary>
    [HttpPost("api/workspaces/{workspaceId:guid}/conversations")]
    public async Task<ActionResult<ConversationDetailResponse>> Start(
        Guid workspaceId, StartConversationRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var inbox = await db.Inboxes
            .SingleOrDefaultAsync(i => i.Id == request.InboxId && i.WorkspaceId == workspaceId);
        if (inbox is null)
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Unknown inbox",
                Detail = "The inbox does not exist in this workspace.",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        var contact = await db.Contacts
            .SingleOrDefaultAsync(c => c.Id == request.ContactId && c.WorkspaceId == workspaceId);
        if (contact is null)
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Unknown contact",
                Detail = "The contact does not exist in this workspace.",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        var now = DateTimeOffset.UtcNow;
        var conversation = new Conversation
        {
            WorkspaceId = workspaceId,
            InboxId = inbox.Id,
            ContactId = contact.Id,
            Subject = request.Subject,
            CreatedAt = now,
            UpdatedAt = now,
            LastMessageAt = now,
            FirstResponseDueAt = inbox.FirstResponseSlaMinutes is { } sla
                ? now.AddMinutes(sla)
                : null,
        };
        conversation.Messages.Add(new Message
        {
            ConversationId = conversation.Id,
            AuthorType = AuthorType.Contact,
            AuthorContactId = contact.Id,
            Body = request.Body,
            CreatedAt = now,
        });
        contact.LastSeenAt = now;

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = conversation.Id }, conversation.ToDetailResponse());
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/conversations")]
    public async Task<ActionResult<List<ConversationSummaryResponse>>> List(Guid workspaceId)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var conversations = await db.Conversations
            .Where(c => c.WorkspaceId == workspaceId)
            .Include(c => c.Tags).ThenInclude(t => t.Tag)
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync();

        return conversations.Select(c => c.ToSummaryResponse()).ToList();
    }

    [HttpGet("api/conversations/{id:guid}")]
    public async Task<ActionResult<ConversationDetailResponse>> GetById(Guid id)
    {
        var conversation = await db.Conversations
            .Include(c => c.Messages)
            .Include(c => c.Tags).ThenInclude(t => t.Tag)
            .SingleOrDefaultAsync(c => c.Id == id);

        return conversation is null ? NotFound() : conversation.ToDetailResponse();
    }
}
