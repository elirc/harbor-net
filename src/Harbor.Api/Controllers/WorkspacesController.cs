using Harbor.Api.Contracts;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
[Route("api/workspaces")]
public class WorkspacesController(HarborDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<WorkspaceResponse>> Create(CreateWorkspaceRequest request)
    {
        var workspace = new Workspace { Name = request.Name };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = workspace.Id }, workspace.ToResponse());
    }

    [HttpGet]
    public async Task<ActionResult<List<WorkspaceResponse>>> List()
    {
        var workspaces = await db.Workspaces
            .OrderBy(w => w.CreatedAt)
            .Select(w => w.ToResponse())
            .ToListAsync();

        return workspaces;
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<WorkspaceResponse>> GetById(Guid id)
    {
        var workspace = await db.Workspaces.FindAsync(id);
        return workspace is null ? NotFound() : workspace.ToResponse();
    }
}
