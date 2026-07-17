using Harbor.Api.Contracts;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
[Route("api/workspaces/{workspaceId:guid}/inboxes")]
public class InboxesController(HarborDbContext db) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<InboxResponse>> Create(Guid workspaceId, CreateInboxRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var inbox = new Inbox
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            FirstResponseSlaMinutes = request.FirstResponseSlaMinutes,
        };
        db.Inboxes.Add(inbox);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { workspaceId, id = inbox.Id }, inbox.ToResponse());
    }

    [HttpGet]
    public async Task<ActionResult<List<InboxResponse>>> List(Guid workspaceId)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        return await db.Inboxes
            .Where(i => i.WorkspaceId == workspaceId)
            .OrderBy(i => i.CreatedAt)
            .Select(i => i.ToResponse())
            .ToListAsync();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InboxResponse>> GetById(Guid workspaceId, Guid id)
    {
        var inbox = await db.Inboxes
            .SingleOrDefaultAsync(i => i.Id == id && i.WorkspaceId == workspaceId);

        return inbox is null ? NotFound() : inbox.ToResponse();
    }
}
