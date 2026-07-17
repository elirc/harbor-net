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

    /// <summary>Lists conversations with optional filters and full-text-ish search.</summary>
    [HttpGet("api/workspaces/{workspaceId:guid}/conversations")]
    public async Task<ActionResult<List<ConversationSummaryResponse>>> List(
        Guid workspaceId, [FromQuery] ConversationFilterRequest filter)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var query = db.Conversations.Where(c => c.WorkspaceId == workspaceId);

        if (filter.State is { } state)
        {
            query = query.Where(c => c.State == state);
        }

        if (filter.InboxId is { } inboxId)
        {
            query = query.Where(c => c.InboxId == inboxId);
        }

        if (filter.ContactId is { } contactId)
        {
            query = query.Where(c => c.ContactId == contactId);
        }

        if (filter.AssignedTeammateId is { } teammateId)
        {
            query = query.Where(c => c.AssignedTeammateId == teammateId);
        }

        if (filter.AssignedTeamId is { } teamId)
        {
            query = query.Where(c => c.AssignedTeamId == teamId);
        }

        if (filter.Unassigned == true)
        {
            query = query.Where(c => c.AssignedTeammateId == null && c.AssignedTeamId == null);
        }

        if (!string.IsNullOrWhiteSpace(filter.Tag))
        {
            var tagName = filter.Tag.Trim().ToLower();
            query = query.Where(c => c.Tags.Any(ct => ct.Tag!.Name.ToLower() == tagName));
        }

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var needle = filter.Q.Trim().ToLower();
            query = query.Where(c =>
                (c.Subject != null && c.Subject.ToLower().Contains(needle))
                || c.Messages.Any(m =>
                    m.Kind == MessageKind.Reply && m.Body.ToLower().Contains(needle)));
        }

        if (filter.SlaBreached == true)
        {
            var now = DateTimeOffset.UtcNow;
            query = query.Where(c =>
                c.FirstResponseDueAt != null
                && (c.FirstRespondedAt == null
                    ? now > c.FirstResponseDueAt
                    : c.FirstRespondedAt > c.FirstResponseDueAt));
        }

        var conversations = await query
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

    /// <summary>Adds a contact/teammate reply or an internal note to the thread.</summary>
    [HttpPost("api/conversations/{id:guid}/messages")]
    public async Task<ActionResult<MessageResponse>> AddMessage(Guid id, AddMessageRequest request)
    {
        var conversation = await db.Conversations.SingleOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
        {
            return NotFound();
        }

        if (request.Kind == MessageKind.Note && request.AuthorType != AuthorType.Teammate)
        {
            return Problem422("Invalid note author", "Internal notes can only be written by teammates.");
        }

        var message = new Message
        {
            ConversationId = conversation.Id,
            Kind = request.Kind,
            AuthorType = request.AuthorType,
            Body = request.Body,
        };

        var now = DateTimeOffset.UtcNow;
        if (request.AuthorType == AuthorType.Contact)
        {
            if (request.AuthorId != conversation.ContactId)
            {
                return Problem422(
                    "Invalid contact author",
                    "Only the conversation's contact can write contact messages.");
            }

            message.AuthorContactId = request.AuthorId;
            var contact = await db.Contacts.SingleAsync(c => c.Id == conversation.ContactId);
            contact.LastSeenAt = now;
        }
        else
        {
            var teammate = await db.Teammates.FindAsync(request.AuthorId);
            if (teammate is null || teammate.WorkspaceId != conversation.WorkspaceId)
            {
                return Problem422(
                    "Unknown teammate",
                    "The author does not exist in this conversation's workspace.");
            }

            message.AuthorTeammateId = request.AuthorId;
        }

        message.CreatedAt = now;
        conversation.RegisterMessage(message, now);
        db.Messages.Add(message);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = conversation.Id }, message.ToResponse());
    }

    /// <summary>Opens, snoozes, or closes the conversation.</summary>
    [HttpPost("api/conversations/{id:guid}/state")]
    public async Task<ActionResult<ConversationSummaryResponse>> ChangeState(Guid id, ChangeStateRequest request)
    {
        var conversation = await db.Conversations
            .Include(c => c.Tags).ThenInclude(t => t.Tag)
            .SingleOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            switch (request.State)
            {
                case ConversationState.Open:
                    conversation.Open(now);
                    break;
                case ConversationState.Snoozed when request.SnoozedUntil is { } until:
                    conversation.Snooze(until, now);
                    break;
                case ConversationState.Snoozed:
                    return Problem422("Missing snooze time", "Snoozing requires 'snoozedUntil'.");
                case ConversationState.Closed:
                    conversation.Close(now);
                    break;
                default:
                    return Problem422("Unknown state", $"Unsupported conversation state '{request.State}'.");
            }
        }
        catch (DomainException ex)
        {
            return Problem422("Invalid state change", ex.Message);
        }

        await db.SaveChangesAsync();
        return conversation.ToSummaryResponse();
    }

    /// <summary>Assigns the conversation to a teammate or a team, or unassigns it.</summary>
    [HttpPost("api/conversations/{id:guid}/assignment")]
    public async Task<ActionResult<ConversationSummaryResponse>> Assign(Guid id, AssignConversationRequest request)
    {
        if (request.TeammateId is not null && request.TeamId is not null)
        {
            return Problem422(
                "Ambiguous assignment",
                "Provide only one of 'teammateId' or 'teamId', or neither to unassign.");
        }

        var conversation = await db.Conversations
            .Include(c => c.Tags).ThenInclude(t => t.Tag)
            .SingleOrDefaultAsync(c => c.Id == id);
        if (conversation is null)
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        if (request.TeammateId is { } teammateId)
        {
            var teammate = await db.Teammates.FindAsync(teammateId);
            if (teammate is null || teammate.WorkspaceId != conversation.WorkspaceId)
            {
                return Problem422("Unknown teammate", "The teammate does not exist in this workspace.");
            }

            conversation.AssignToTeammate(teammateId, now);
        }
        else if (request.TeamId is { } teamId)
        {
            var team = await db.Teams.FindAsync(teamId);
            if (team is null || team.WorkspaceId != conversation.WorkspaceId)
            {
                return Problem422("Unknown team", "The team does not exist in this workspace.");
            }

            conversation.AssignToTeam(teamId, now);
        }
        else
        {
            conversation.Unassign(now);
        }

        await db.SaveChangesAsync();
        return conversation.ToSummaryResponse();
    }

    private ObjectResult Problem422(string title, string detail) =>
        UnprocessableEntity(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status422UnprocessableEntity,
        });
}
