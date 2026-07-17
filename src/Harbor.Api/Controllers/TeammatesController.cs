using Harbor.Api.Contracts;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
public class TeammatesController(HarborDbContext db) : ControllerBase
{
    [HttpPost("api/workspaces/{workspaceId:guid}/teammates")]
    public async Task<ActionResult<TeammateResponse>> Create(Guid workspaceId, CreateTeammateRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Teammates.AnyAsync(t => t.WorkspaceId == workspaceId && t.Email == email))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Duplicate teammate email",
                Detail = $"A teammate with email '{email}' already exists in this workspace.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        var teammate = new Teammate { WorkspaceId = workspaceId, Name = request.Name, Email = email };
        db.Teammates.Add(teammate);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = teammate.Id }, teammate.ToResponse());
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/teammates")]
    public async Task<ActionResult<List<TeammateResponse>>> List(Guid workspaceId)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        return await db.Teammates
            .Where(t => t.WorkspaceId == workspaceId)
            .OrderBy(t => t.Name)
            .Select(t => t.ToResponse())
            .ToListAsync();
    }

    [HttpGet("api/teammates/{id:guid}")]
    public async Task<ActionResult<TeammateResponse>> GetById(Guid id)
    {
        var teammate = await db.Teammates.FindAsync(id);
        return teammate is null ? NotFound() : teammate.ToResponse();
    }
}
