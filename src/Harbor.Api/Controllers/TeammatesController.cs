using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
public class TeammatesController(HarborDbContext db) : ControllerBase
{
    /// <summary>Creates a teammate and mints their API key (admin only).</summary>
    [HttpPost("api/workspaces/{workspaceId:guid}/teammates")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TeammateCreatedResponse>> Create(Guid workspaceId, CreateTeammateRequest request)
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

        var apiKey = ApiKeys.Generate();
        var teammate = new Teammate
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Email = email,
            Role = request.Role,
            ApiKeyHash = ApiKeys.Hash(apiKey),
        };
        db.Teammates.Add(teammate);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = teammate.Id }, teammate.ToCreatedResponse(apiKey));
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
        var teammate = await db.Teammates
            .SingleOrDefaultAsync(t => t.Id == id && t.WorkspaceId == User.GetWorkspaceId());
        return teammate is null ? NotFound() : teammate.ToResponse();
    }
}
