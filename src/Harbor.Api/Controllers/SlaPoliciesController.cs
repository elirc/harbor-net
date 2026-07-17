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
public class SlaPoliciesController(HarborDbContext db) : ControllerBase
{
    [HttpPost("api/workspaces/{workspaceId:guid}/sla-policies")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SlaPolicyResponse>> Create(
        Guid workspaceId, CreateSlaPolicyRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        if (await ValidateScopeAsync(
                workspaceId, request.InboxId, request.Priority,
                request.FirstResponseMinutes, request.ResolutionMinutes, null) is { } problem)
        {
            return problem;
        }

        var policy = new SlaPolicy
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            InboxId = request.InboxId,
            Priority = request.Priority,
            FirstResponseMinutes = request.FirstResponseMinutes,
            ResolutionMinutes = request.ResolutionMinutes,
        };
        db.SlaPolicies.Add(policy);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = policy.Id }, policy.ToResponse());
    }

    /// <summary>Lists SLA policies, most specific first — the order they are matched in.</summary>
    [HttpGet("api/workspaces/{workspaceId:guid}/sla-policies")]
    public async Task<ActionResult<List<SlaPolicyResponse>>> List(Guid workspaceId)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var policies = await db.SlaPolicies
            .Where(p => p.WorkspaceId == workspaceId)
            .ToListAsync();

        return policies
            .OrderByDescending(p => p.Specificity)
            .ThenBy(p => p.CreatedAt)
            .Select(p => p.ToResponse())
            .ToList();
    }

    [HttpGet("api/sla-policies/{id:guid}")]
    public async Task<ActionResult<SlaPolicyResponse>> GetById(Guid id)
    {
        var policy = await db.SlaPolicies
            .SingleOrDefaultAsync(p => p.Id == id && p.WorkspaceId == User.GetWorkspaceId());
        return policy is null ? NotFound() : policy.ToResponse();
    }

    [HttpPut("api/sla-policies/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SlaPolicyResponse>> Update(Guid id, UpdateSlaPolicyRequest request)
    {
        var policy = await db.SlaPolicies
            .SingleOrDefaultAsync(p => p.Id == id && p.WorkspaceId == User.GetWorkspaceId());
        if (policy is null)
        {
            return NotFound();
        }

        if (await ValidateScopeAsync(
                policy.WorkspaceId, request.InboxId, request.Priority,
                request.FirstResponseMinutes, request.ResolutionMinutes, id) is { } problem)
        {
            return problem;
        }

        policy.Name = request.Name;
        policy.InboxId = request.InboxId;
        policy.Priority = request.Priority;
        policy.FirstResponseMinutes = request.FirstResponseMinutes;
        policy.ResolutionMinutes = request.ResolutionMinutes;
        await db.SaveChangesAsync();

        return policy.ToResponse();
    }

    [HttpDelete("api/sla-policies/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var policy = await db.SlaPolicies
            .SingleOrDefaultAsync(p => p.Id == id && p.WorkspaceId == User.GetWorkspaceId());
        if (policy is null)
        {
            return NotFound();
        }

        db.SlaPolicies.Remove(policy);
        await db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>
    /// Rejects policies that target nothing, point at a foreign inbox, or
    /// duplicate an existing scope. Scope uniqueness cannot be a database
    /// constraint because SQL treats NULLs as distinct, and "any inbox" /
    /// "any priority" are exactly the null cases.
    /// </summary>
    private async Task<ObjectResult?> ValidateScopeAsync(
        Guid workspaceId, Guid? inboxId, ConversationPriority? priority,
        int? firstResponseMinutes, int? resolutionMinutes, Guid? excludeId)
    {
        if (firstResponseMinutes is null && resolutionMinutes is null)
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "SLA policy has no targets",
                Detail = "Set firstResponseMinutes, resolutionMinutes, or both.",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        if (inboxId is { } id
            && !await db.Inboxes.AnyAsync(i => i.Id == id && i.WorkspaceId == workspaceId))
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Unknown inbox",
                Detail = "The inbox does not exist in this workspace.",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        var duplicate = await db.SlaPolicies.AnyAsync(p =>
            p.WorkspaceId == workspaceId
            && p.InboxId == inboxId
            && p.Priority == priority
            && (excludeId == null || p.Id != excludeId));
        if (duplicate)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Duplicate SLA policy scope",
                Detail = "Another policy already targets this inbox and priority combination.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        return null;
    }
}
