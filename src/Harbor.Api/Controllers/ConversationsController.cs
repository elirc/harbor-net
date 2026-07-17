using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
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
            Priority = request.Priority ?? ConversationPriority.Normal,
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
            Body = request.Body,
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
            db, workspaceId, WebhookEventType.ConversationCreated,
            conversation.ToSummaryResponse(), now);
        if (conversation.AssignedTeammateId is not null)
        {
            await Webhooks.PublishAsync(
                db, workspaceId, WebhookEventType.ConversationAssigned,
                conversation.ToSummaryResponse(), now);
        }

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

        if (filter.Priority is { } priority)
        {
            query = query.Where(c => c.Priority == priority);
        }

        if (filter.SlaBreached == true)
        {
            // Mirrors Conversation.IsSlaBreached, expressed in LINQ so the
            // database does the filtering.
            var now = DateTimeOffset.UtcNow;
            query = query.Where(c =>
                (c.FirstResponseDueAt != null
                    && (c.FirstRespondedAt == null
                        ? now > c.FirstResponseDueAt
                        : c.FirstRespondedAt > c.FirstResponseDueAt))
                || (c.ResolutionDueAt != null
                    && (c.FirstResolvedAt == null
                        ? now > c.ResolutionDueAt
                        : c.FirstResolvedAt > c.ResolutionDueAt)));
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
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());

        return conversation is null ? NotFound() : conversation.ToDetailResponse();
    }

    /// <summary>Adds a contact/teammate reply or an internal note to the thread.</summary>
    [HttpPost("api/conversations/{id:guid}/messages")]
    public async Task<ActionResult<MessageResponse>> AddMessage(Guid id, AddMessageRequest request)
    {
        var conversation = await db.Conversations
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
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

        // A first reply that lands after the target breaches it on the spot.
        await SlaBreaches.DetectAsync(db, conversation, now);
        await Webhooks.PublishAsync(
            db, conversation.WorkspaceId, WebhookEventType.MessageCreated,
            message.ToResponse(), now);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = conversation.Id }, message.ToResponse());
    }

    /// <summary>Opens, snoozes, or closes the conversation.</summary>
    [HttpPost("api/conversations/{id:guid}/state")]
    public async Task<ActionResult<ConversationSummaryResponse>> ChangeState(Guid id, ChangeStateRequest request)
    {
        var conversation = await db.Conversations
            .Include(c => c.Tags).ThenInclude(t => t.Tag)
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (conversation is null)
        {
            return NotFound();
        }

        // DomainException (e.g. snoozing into the past) is translated to a
        // 422 ProblemDetails response by DomainExceptionHandler.
        var now = DateTimeOffset.UtcNow;
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

        // A close that lands after the resolution target breaches it on the spot.
        await SlaBreaches.DetectAsync(db, conversation, now);
        if (request.State == ConversationState.Closed)
        {
            await Webhooks.PublishAsync(
                db, conversation.WorkspaceId, WebhookEventType.ConversationClosed,
                conversation.ToSummaryResponse(), now);
        }

        await db.SaveChangesAsync();
        return conversation.ToSummaryResponse();
    }

    /// <summary>
    /// Changes priority and re-stamps SLA targets from the policy that now
    /// governs the conversation. The clock still runs from creation, so
    /// escalating an old conversation can put it immediately past due.
    /// </summary>
    [HttpPut("api/conversations/{id:guid}/priority")]
    public async Task<ActionResult<ConversationSummaryResponse>> SetPriority(
        Guid id, SetPriorityRequest request)
    {
        var conversation = await db.Conversations
            .Include(c => c.Tags).ThenInclude(t => t.Tag)
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (conversation is null)
        {
            return NotFound();
        }

        var inbox = await db.Inboxes.SingleAsync(i => i.Id == conversation.InboxId);
        var now = DateTimeOffset.UtcNow;

        conversation.SetPriority(request.Priority, now);
        await SlaPolicies.ApplyAsync(db, conversation, inbox);
        await SlaBreaches.DetectAsync(db, conversation, now);
        await db.SaveChangesAsync();

        return conversation.ToSummaryResponse();
    }

    /// <summary>
    /// Records breaches for conversations sitting past a target. The reply and
    /// close paths catch breaches as they happen; this catches the ones where
    /// nothing happens at all, and is safe to call repeatedly.
    /// </summary>
    [HttpPost("api/workspaces/{workspaceId:guid}/sla/evaluate")]
    public async Task<ActionResult<List<SlaBreachEventResponse>>> EvaluateSla(Guid workspaceId)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = await db.Conversations
            .Where(c => c.WorkspaceId == workspaceId
                && (c.FirstResponseDueAt != null || c.ResolutionDueAt != null))
            .ToListAsync();

        var recorded = await SlaBreaches.DetectAsync(db, candidates, now);
        await db.SaveChangesAsync();

        return recorded.Select(b => b.ToResponse()).ToList();
    }

    /// <summary>Every SLA target this conversation has missed.</summary>
    [HttpGet("api/conversations/{id:guid}/sla-breaches")]
    public async Task<ActionResult<List<SlaBreachEventResponse>>> ListSlaBreaches(Guid id)
    {
        var exists = await db.Conversations
            .AnyAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (!exists)
        {
            return NotFound();
        }

        return await db.SlaBreachEvents
            .Where(b => b.ConversationId == id)
            .OrderBy(b => b.CreatedAt)
            .Select(b => b.ToResponse())
            .ToListAsync();
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
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (conversation is null)
        {
            return NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        var previousTeammateId = conversation.AssignedTeammateId;
        var previousTeamId = conversation.AssignedTeamId;

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

        db.AssignmentEvents.Add(new AssignmentEvent
        {
            ConversationId = conversation.Id,
            Kind = AssignmentKind.Manual,
            ActorTeammateId = User.GetTeammateId(),
            FromTeammateId = previousTeammateId,
            FromTeamId = previousTeamId,
            ToTeammateId = conversation.AssignedTeammateId,
            ToTeamId = conversation.AssignedTeamId,
            CreatedAt = now,
        });

        // Unassigning is not an assignment event for subscribers.
        if (conversation.AssignedTeammateId is not null || conversation.AssignedTeamId is not null)
        {
            await Webhooks.PublishAsync(
                db, conversation.WorkspaceId, WebhookEventType.ConversationAssigned,
                conversation.ToSummaryResponse(), now);
        }

        await db.SaveChangesAsync();
        return conversation.ToSummaryResponse();
    }

    /// <summary>Audit trail of every assignment change, oldest first.</summary>
    [HttpGet("api/conversations/{id:guid}/assignment-events")]
    public async Task<ActionResult<List<AssignmentEventResponse>>> ListAssignmentEvents(Guid id)
    {
        var exists = await db.Conversations
            .AnyAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (!exists)
        {
            return NotFound();
        }

        return await db.AssignmentEvents
            .Where(a => a.ConversationId == id)
            .OrderBy(a => a.CreatedAt)
            .Select(a => a.ToResponse())
            .ToListAsync();
    }

    private ObjectResult Problem422(string title, string detail) =>
        UnprocessableEntity(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status422UnprocessableEntity,
        });
}
