using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Controllers;

/// <summary>
/// The public help center: no API key, published articles only.
///
/// Every query here filters on Status == Published rather than relying on a
/// caller to ask for the right thing. This is the one place in the API that
/// answers to anyone at all, so a draft leaking would be a disclosure bug, not
/// a display bug. Article bodies are keyed by slug, and a draft's slug returns
/// 404 exactly like a slug that never existed — the response cannot be used to
/// probe for unpublished work.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/public/workspaces/{workspaceId:guid}")]
public class PublicHelpCenterController(HarborDbContext db) : ControllerBase
{
    [HttpGet("collections")]
    public async Task<ActionResult<List<CollectionResponse>>> Collections(
        Guid workspaceId, [FromQuery] PageRequest paging)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        // Collections with nothing published in them are not part of a public
        // help center; listing them would advertise work in progress.
        return await db.ArticleCollections
            .Where(c => c.WorkspaceId == workspaceId
                && c.Articles.Any(a => a.Status == ArticleStatus.Published))
            .OrderBy(c => c.Name)
            .Select(c => new CollectionResponse(
                c.Id, c.WorkspaceId, c.Name, c.Slug, c.Description,
                c.Articles.Count(a => a.Status == ArticleStatus.Published), c.CreatedAt))
            .ToPageAsync(paging, Response);
    }

    /// <summary>Published articles, optionally searched or filtered by collection.</summary>
    [HttpGet("articles")]
    public async Task<ActionResult<List<PublicArticleResponse>>> Articles(
        Guid workspaceId, [FromQuery] string? q, [FromQuery] Guid? collectionId,
        [FromQuery] PageRequest paging)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var query = db.Articles
            .Where(a => a.WorkspaceId == workspaceId && a.Status == ArticleStatus.Published);

        if (collectionId is { } id)
        {
            query = query.Where(a => a.CollectionId == id);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim().ToLower();
            query = query.Where(a =>
                a.Title.ToLower().Contains(needle) || a.Body.ToLower().Contains(needle));
        }

        return await query
            .OrderBy(a => a.Title)
            .Select(a => a.ToPublicResponse())
            .ToPageAsync(paging, Response);
    }

    [HttpGet("articles/{slug}")]
    public async Task<ActionResult<PublicArticleResponse>> Article(Guid workspaceId, string slug)
    {
        var article = await db.Articles
            .SingleOrDefaultAsync(a => a.WorkspaceId == workspaceId
                && a.Slug == slug
                && a.Status == ArticleStatus.Published);

        return article is null ? NotFound() : article.ToPublicResponse();
    }
}
