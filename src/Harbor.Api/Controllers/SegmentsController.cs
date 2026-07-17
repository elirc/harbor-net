using System.Text.Json;
using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

/// <summary>
/// Dynamic contact segments. Rules are stored as JSON and compiled into a
/// query on use, so membership is always evaluated against the contacts as
/// they are now — nothing to refresh, nothing to go stale.
/// </summary>
[ApiController]
public class SegmentsController(HarborDbContext db) : ControllerBase
{
    /// <summary>Rules are stored exactly as the API speaks them.</summary>
    public static readonly JsonSerializerOptions RulesJson =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        };

    [HttpPost("api/workspaces/{workspaceId:guid}/segments")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SegmentResponse>> Create(
        Guid workspaceId, CreateSegmentRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        if (ValidateRules(request.Rules) is { } problem)
        {
            return problem;
        }

        if (await db.Segments.AnyAsync(s => s.WorkspaceId == workspaceId && s.Name == request.Name))
        {
            return NameConflict(request.Name);
        }

        var segment = new Segment
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            RulesJson = JsonSerializer.Serialize(request.Rules, RulesJson),
        };
        db.Segments.Add(segment);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetById), new { id = segment.Id }, segment.ToResponse(request.Rules));
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/segments")]
    public async Task<ActionResult<List<SegmentResponse>>> List(Guid workspaceId)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var segments = await db.Segments
            .Where(s => s.WorkspaceId == workspaceId)
            .OrderBy(s => s.Name)
            .ToListAsync();

        return segments.Select(s => s.ToResponse(Rules(s))).ToList();
    }

    [HttpGet("api/segments/{id:guid}")]
    public async Task<ActionResult<SegmentResponse>> GetById(Guid id)
    {
        var segment = await FindAsync(id);
        return segment is null ? NotFound() : segment.ToResponse(Rules(segment));
    }

    [HttpPut("api/segments/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<SegmentResponse>> Update(Guid id, UpdateSegmentRequest request)
    {
        var segment = await FindAsync(id);
        if (segment is null)
        {
            return NotFound();
        }

        if (ValidateRules(request.Rules) is { } problem)
        {
            return problem;
        }

        if (await db.Segments.AnyAsync(s =>
                s.WorkspaceId == segment.WorkspaceId && s.Name == request.Name && s.Id != id))
        {
            return NameConflict(request.Name);
        }

        segment.Name = request.Name;
        segment.RulesJson = JsonSerializer.Serialize(request.Rules, RulesJson);
        segment.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return segment.ToResponse(request.Rules);
    }

    [HttpDelete("api/segments/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var segment = await FindAsync(id);
        if (segment is null)
        {
            return NotFound();
        }

        db.Segments.Remove(segment);
        await db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>The contacts currently matching the segment.</summary>
    [HttpGet("api/segments/{id:guid}/contacts")]
    public async Task<ActionResult<List<ContactResponse>>> Contacts(Guid id)
    {
        var segment = await FindAsync(id);
        if (segment is null)
        {
            return NotFound();
        }

        var contacts = await db.Contacts
            .Where(c => c.WorkspaceId == segment.WorkspaceId)
            .Where(SegmentCompiler.Compile(Rules(segment)))
            .OrderBy(c => c.Name)
            .ToListAsync();

        return contacts.Select(c => c.ToResponse()).ToList();
    }

    /// <summary>
    /// The contact-id subquery for a segment, used by the conversation filter.
    /// Kept as a query rather than a materialized id list so the database does
    /// the work in one statement.
    /// </summary>
    public static IQueryable<Guid> ContactIdsQuery(
        HarborDbContext db, Guid workspaceId, Segment segment) =>
        db.Contacts
            .Where(c => c.WorkspaceId == workspaceId)
            .Where(SegmentCompiler.Compile(RulesOf(segment)))
            .Select(c => c.Id);

    public static SegmentRuleSet RulesOf(Segment segment) =>
        JsonSerializer.Deserialize<SegmentRuleSet>(segment.RulesJson, RulesJson)
        ?? new SegmentRuleSet(SegmentMatch.All, []);

    private static SegmentRuleSet Rules(Segment segment) => RulesOf(segment);

    private Task<Segment?> FindAsync(Guid id) =>
        db.Segments.SingleOrDefaultAsync(s => s.Id == id && s.WorkspaceId == User.GetWorkspaceId());

    /// <summary>
    /// Compiles the rules purely to find out whether they are usable, so a
    /// broken segment is rejected at write time instead of throwing later on
    /// every read.
    /// </summary>
    private ObjectResult? ValidateRules(SegmentRuleSet rules)
    {
        try
        {
            SegmentCompiler.Validate(rules);
            return null;
        }
        catch (SegmentRuleException ex)
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Invalid segment rules",
                Detail = ex.Message,
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }
    }

    private ObjectResult NameConflict(string name) =>
        Conflict(new ProblemDetails
        {
            Title = "Duplicate segment name",
            Detail = $"A segment named '{name}' already exists in this workspace.",
            Status = StatusCodes.Status409Conflict,
        });
}
