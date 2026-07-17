using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
public class WorkspacesController(HarborDbContext db) : ControllerBase
{
    /// <summary>
    /// Bootstraps a workspace with its first admin teammate. Anonymous by
    /// design: this is the entry point that mints the first API key.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    public async Task<ActionResult<CreateWorkspaceResponse>> Create(CreateWorkspaceRequest request)
    {
        var workspace = new Workspace { Name = request.Name };
        var apiKey = ApiKeys.Generate();
        var admin = new Teammate
        {
            WorkspaceId = workspace.Id,
            Name = request.AdminName,
            Email = request.AdminEmail.Trim().ToLowerInvariant(),
            Role = TeammateRole.Admin,
            ApiKeyHash = ApiKeys.Hash(apiKey),
        };

        db.Workspaces.Add(workspace);
        db.Teammates.Add(admin);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetById),
            new { workspaceId = workspace.Id },
            new CreateWorkspaceResponse(workspace.ToResponse(), admin.ToResponse(), apiKey));
    }

    /// <summary>Lists the caller's workspace (API keys are single-tenant).</summary>
    [HttpGet]
    public async Task<ActionResult<List<WorkspaceResponse>>> List([FromQuery] PageRequest paging)
    {
        var workspaceId = User.GetWorkspaceId();
        return await db.Workspaces
            .Where(w => w.Id == workspaceId)
            .OrderBy(w => w.Name)
            .Select(w => w.ToResponse())
            .ToPageAsync(paging, Response);
    }

    [HttpGet("{workspaceId:guid}")]
    public async Task<ActionResult<WorkspaceResponse>> GetById(Guid workspaceId)
    {
        var workspace = await db.Workspaces.FindAsync(workspaceId);
        return workspace is null ? NotFound() : workspace.ToResponse();
    }
}
