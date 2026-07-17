using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
public class TeamsController(HarborDbContext db) : ControllerBase
{
    [HttpPost("api/workspaces/{workspaceId:guid}/teams")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TeamResponse>> Create(Guid workspaceId, CreateTeamRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        if (await db.Teams.AnyAsync(t => t.WorkspaceId == workspaceId && t.Name == request.Name))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Duplicate team name",
                Detail = $"A team named '{request.Name}' already exists in this workspace.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        var team = new Team { WorkspaceId = workspaceId, Name = request.Name };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = team.Id }, team.ToResponse());
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/teams")]
    public async Task<ActionResult<List<TeamResponse>>> List(Guid workspaceId, [FromQuery] PageRequest paging)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var teams = await db.Teams
            .Where(t => t.WorkspaceId == workspaceId)
            .Include(t => t.Members)
            .OrderBy(t => t.Name)
            .ToPageAsync(paging, Response);

        return teams.Select(t => t.ToResponse()).ToList();
    }

    [HttpGet("api/teams/{id:guid}")]
    public async Task<ActionResult<TeamResponse>> GetById(Guid id)
    {
        var team = await db.Teams
            .Include(t => t.Members)
            .SingleOrDefaultAsync(t => t.Id == id && t.WorkspaceId == User.GetWorkspaceId());
        return team is null ? NotFound() : team.ToResponse();
    }

    [HttpPost("api/teams/{id:guid}/members")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TeamResponse>> AddMember(Guid id, AddTeamMemberRequest request)
    {
        var team = await db.Teams
            .Include(t => t.Members)
            .SingleOrDefaultAsync(t => t.Id == id && t.WorkspaceId == User.GetWorkspaceId());
        if (team is null)
        {
            return NotFound();
        }

        var teammate = await db.Teammates.FindAsync(request.TeammateId);
        if (teammate is null || teammate.WorkspaceId != team.WorkspaceId)
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Unknown teammate",
                Detail = "The teammate does not exist in this team's workspace.",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        if (team.Members.All(m => m.TeammateId != request.TeammateId))
        {
            team.Members.Add(new TeamMembership { TeamId = id, TeammateId = request.TeammateId });
            await db.SaveChangesAsync();
        }

        return team.ToResponse();
    }

    [HttpDelete("api/teams/{id:guid}/members/{teammateId:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RemoveMember(Guid id, Guid teammateId)
    {
        var membership = await db.TeamMemberships
            .SingleOrDefaultAsync(m =>
                m.TeamId == id
                && m.TeammateId == teammateId
                && m.Team!.WorkspaceId == User.GetWorkspaceId());
        if (membership is null)
        {
            return NotFound();
        }

        db.TeamMemberships.Remove(membership);
        await db.SaveChangesAsync();

        return NoContent();
    }
}
