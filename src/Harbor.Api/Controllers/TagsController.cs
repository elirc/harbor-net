using Harbor.Api.Contracts;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

[ApiController]
public class TagsController(HarborDbContext db) : ControllerBase
{
    [HttpPost("api/workspaces/{workspaceId:guid}/tags")]
    public async Task<ActionResult<TagResponse>> Create(Guid workspaceId, CreateTagRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var name = request.Name.Trim().ToLowerInvariant();
        if (await db.Tags.AnyAsync(t => t.WorkspaceId == workspaceId && t.Name == name))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Duplicate tag",
                Detail = $"A tag named '{name}' already exists in this workspace.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        var tag = new Tag { WorkspaceId = workspaceId, Name = name };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(List), new { workspaceId }, tag.ToResponse());
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/tags")]
    public async Task<ActionResult<List<TagResponse>>> List(Guid workspaceId)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        return await db.Tags
            .Where(t => t.WorkspaceId == workspaceId)
            .OrderBy(t => t.Name)
            .Select(t => t.ToResponse())
            .ToListAsync();
    }

    [HttpDelete("api/tags/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var tag = await db.Tags.FindAsync(id);
        if (tag is null)
        {
            return NotFound();
        }

        db.Tags.Remove(tag);
        await db.SaveChangesAsync();

        return NoContent();
    }

    /// <summary>Tags a conversation (idempotent).</summary>
    [HttpPut("api/conversations/{conversationId:guid}/tags/{tagId:guid}")]
    public async Task<IActionResult> AddToConversation(Guid conversationId, Guid tagId)
    {
        var conversation = await db.Conversations.FindAsync(conversationId);
        if (conversation is null)
        {
            return NotFound();
        }

        var tag = await db.Tags.FindAsync(tagId);
        if (tag is null || tag.WorkspaceId != conversation.WorkspaceId)
        {
            return UnprocessableEntity(new ProblemDetails
            {
                Title = "Unknown tag",
                Detail = "The tag does not exist in this conversation's workspace.",
                Status = StatusCodes.Status422UnprocessableEntity,
            });
        }

        var exists = await db.ConversationTags
            .AnyAsync(ct => ct.ConversationId == conversationId && ct.TagId == tagId);
        if (!exists)
        {
            db.ConversationTags.Add(new ConversationTag { ConversationId = conversationId, TagId = tagId });
            await db.SaveChangesAsync();
        }

        return NoContent();
    }

    [HttpDelete("api/conversations/{conversationId:guid}/tags/{tagId:guid}")]
    public async Task<IActionResult> RemoveFromConversation(Guid conversationId, Guid tagId)
    {
        var link = await db.ConversationTags
            .SingleOrDefaultAsync(ct => ct.ConversationId == conversationId && ct.TagId == tagId);
        if (link is null)
        {
            return NotFound();
        }

        db.ConversationTags.Remove(link);
        await db.SaveChangesAsync();

        return NoContent();
    }
}
