using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
public class CannedRepliesController(HarborDbContext db) : ControllerBase
{
    [HttpPost("api/workspaces/{workspaceId:guid}/canned-replies")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CannedReplyResponse>> Create(
        Guid workspaceId, CreateCannedReplyRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var shortcut = request.Shortcut.Trim().ToLowerInvariant();
        if (await db.CannedReplies.AnyAsync(r => r.WorkspaceId == workspaceId && r.Shortcut == shortcut))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Duplicate shortcut",
                Detail = $"A canned reply with shortcut '{shortcut}' already exists in this workspace.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        var reply = new CannedReply
        {
            WorkspaceId = workspaceId,
            Shortcut = shortcut,
            Title = request.Title,
            Body = request.Body,
        };
        db.CannedReplies.Add(reply);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = reply.Id }, reply.ToResponse());
    }

    /// <summary>Lists canned replies; optional search on shortcut/title/body.</summary>
    [HttpGet("api/workspaces/{workspaceId:guid}/canned-replies")]
    public async Task<ActionResult<List<CannedReplyResponse>>> List(Guid workspaceId, [FromQuery] string? q)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var query = db.CannedReplies.Where(r => r.WorkspaceId == workspaceId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(r =>
                r.Shortcut.ToLower().Contains(needle)
                || r.Title.ToLower().Contains(needle)
                || r.Body.ToLower().Contains(needle));
        }

        return await query
            .OrderBy(r => r.Shortcut)
            .Select(r => r.ToResponse())
            .ToListAsync();
    }

    [HttpGet("api/canned-replies/{id:guid}")]
    public async Task<ActionResult<CannedReplyResponse>> GetById(Guid id)
    {
        var reply = await db.CannedReplies
            .SingleOrDefaultAsync(r => r.Id == id && r.WorkspaceId == User.GetWorkspaceId());
        return reply is null ? NotFound() : reply.ToResponse();
    }

    [HttpPut("api/canned-replies/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CannedReplyResponse>> Update(Guid id, UpdateCannedReplyRequest request)
    {
        var reply = await db.CannedReplies
            .SingleOrDefaultAsync(r => r.Id == id && r.WorkspaceId == User.GetWorkspaceId());
        if (reply is null)
        {
            return NotFound();
        }

        var shortcut = request.Shortcut.Trim().ToLowerInvariant();
        var duplicate = await db.CannedReplies.AnyAsync(r =>
            r.WorkspaceId == reply.WorkspaceId && r.Shortcut == shortcut && r.Id != id);
        if (duplicate)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Duplicate shortcut",
                Detail = $"A canned reply with shortcut '{shortcut}' already exists in this workspace.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        reply.Shortcut = shortcut;
        reply.Title = request.Title;
        reply.Body = request.Body;
        await db.SaveChangesAsync();

        return reply.ToResponse();
    }

    [HttpDelete("api/canned-replies/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var reply = await db.CannedReplies
            .SingleOrDefaultAsync(r => r.Id == id && r.WorkspaceId == User.GetWorkspaceId());
        if (reply is null)
        {
            return NotFound();
        }

        db.CannedReplies.Remove(reply);
        await db.SaveChangesAsync();

        return NoContent();
    }
}
