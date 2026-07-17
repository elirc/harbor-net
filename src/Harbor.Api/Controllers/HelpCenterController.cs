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
/// Help-center authoring. Teammates can read everything including drafts;
/// writing is admin-only. The public, unauthenticated view lives in
/// <see cref="PublicHelpCenterController"/>.
/// </summary>
[ApiController]
public class HelpCenterController(HarborDbContext db) : ControllerBase
{
    // --- Collections -----------------------------------------------------

    [HttpPost("api/workspaces/{workspaceId:guid}/collections")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CollectionResponse>> CreateCollection(
        Guid workspaceId, CreateCollectionRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        if (SlugOrProblem(request.Slug, request.Name) is not { } slug)
        {
            return Problem422("Unusable slug", "The name and slug contain no usable characters.");
        }

        if (await db.ArticleCollections.AnyAsync(c => c.WorkspaceId == workspaceId && c.Slug == slug))
        {
            return SlugConflict(slug);
        }

        var collection = new ArticleCollection
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Slug = slug,
            Description = request.Description,
        };
        db.ArticleCollections.Add(collection);
        await db.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetCollection), new { id = collection.Id }, collection.ToResponse(0));
    }

    [HttpGet("api/workspaces/{workspaceId:guid}/collections")]
    public async Task<ActionResult<List<CollectionResponse>>> ListCollections(
        Guid workspaceId, [FromQuery] PageRequest paging)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        return await db.ArticleCollections
            .Where(c => c.WorkspaceId == workspaceId)
            .OrderBy(c => c.Name)
            .Select(c => new CollectionResponse(
                c.Id, c.WorkspaceId, c.Name, c.Slug, c.Description,
                c.Articles.Count(a => a.Status == ArticleStatus.Published), c.CreatedAt))
            .ToPageAsync(paging, Response);
    }

    [HttpGet("api/collections/{id:guid}")]
    public async Task<ActionResult<CollectionResponse>> GetCollection(Guid id)
    {
        var collection = await db.ArticleCollections
            .Include(c => c.Articles)
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());

        return collection is null
            ? NotFound()
            : collection.ToResponse(collection.Articles.Count(a => a.Status == ArticleStatus.Published));
    }

    [HttpPut("api/collections/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<CollectionResponse>> UpdateCollection(
        Guid id, UpdateCollectionRequest request)
    {
        var collection = await db.ArticleCollections
            .Include(c => c.Articles)
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (collection is null)
        {
            return NotFound();
        }

        if (SlugOrProblem(request.Slug, request.Name) is not { } slug)
        {
            return Problem422("Unusable slug", "The name and slug contain no usable characters.");
        }

        if (await db.ArticleCollections.AnyAsync(c =>
                c.WorkspaceId == collection.WorkspaceId && c.Slug == slug && c.Id != id))
        {
            return SlugConflict(slug);
        }

        collection.Name = request.Name;
        collection.Slug = slug;
        collection.Description = request.Description;
        await db.SaveChangesAsync();

        return collection.ToResponse(collection.Articles.Count(a => a.Status == ArticleStatus.Published));
    }

    [HttpDelete("api/collections/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteCollection(Guid id)
    {
        var collection = await db.ArticleCollections
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (collection is null)
        {
            return NotFound();
        }

        if (await db.Articles.AnyAsync(a => a.CollectionId == id))
        {
            return Conflict(new ProblemDetails
            {
                Title = "Collection is not empty",
                Detail = "Move or delete the collection's articles before deleting it.",
                Status = StatusCodes.Status409Conflict,
            });
        }

        db.ArticleCollections.Remove(collection);
        await db.SaveChangesAsync();

        return NoContent();
    }

    // --- Articles --------------------------------------------------------

    /// <summary>Creates a draft. Publishing is a deliberate second step.</summary>
    [HttpPost("api/workspaces/{workspaceId:guid}/articles")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ArticleResponse>> CreateArticle(
        Guid workspaceId, CreateArticleRequest request)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        if (!await db.ArticleCollections.AnyAsync(c =>
                c.Id == request.CollectionId && c.WorkspaceId == workspaceId))
        {
            return Problem422(
                "Unknown collection", "The collection does not exist in this workspace.");
        }

        if (SlugOrProblem(request.Slug, request.Title) is not { } slug)
        {
            return Problem422("Unusable slug", "The title and slug contain no usable characters.");
        }

        if (await db.Articles.AnyAsync(a => a.WorkspaceId == workspaceId && a.Slug == slug))
        {
            return SlugConflict(slug);
        }

        var now = DateTimeOffset.UtcNow;
        var article = new Article
        {
            WorkspaceId = workspaceId,
            CollectionId = request.CollectionId,
            Title = request.Title,
            Slug = slug,
            Body = request.Body,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Articles.Add(article);
        await db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetArticle), new { id = article.Id }, article.ToResponse());
    }

    /// <summary>Lists articles including drafts; teammates only.</summary>
    [HttpGet("api/workspaces/{workspaceId:guid}/articles")]
    public async Task<ActionResult<List<ArticleResponse>>> ListArticles(
        Guid workspaceId, [FromQuery] ArticleFilterRequest filter)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId))
        {
            return NotFound();
        }

        var query = db.Articles.Where(a => a.WorkspaceId == workspaceId);

        if (filter.Status is { } status)
        {
            query = query.Where(a => a.Status == status);
        }

        if (filter.CollectionId is { } collectionId)
        {
            query = query.Where(a => a.CollectionId == collectionId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var needle = filter.Q.Trim().ToLower();
            query = query.Where(a =>
                a.Title.ToLower().Contains(needle) || a.Body.ToLower().Contains(needle));
        }

        return await query
            .OrderBy(a => a.Title)
            .Select(a => a.ToResponse())
            .ToPageAsync(filter, Response);
    }

    [HttpGet("api/articles/{id:guid}")]
    public async Task<ActionResult<ArticleResponse>> GetArticle(Guid id)
    {
        var article = await FindArticleAsync(id);
        return article is null ? NotFound() : article.ToResponse();
    }

    [HttpPut("api/articles/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ArticleResponse>> UpdateArticle(Guid id, UpdateArticleRequest request)
    {
        var article = await FindArticleAsync(id);
        if (article is null)
        {
            return NotFound();
        }

        if (!await db.ArticleCollections.AnyAsync(c =>
                c.Id == request.CollectionId && c.WorkspaceId == article.WorkspaceId))
        {
            return Problem422(
                "Unknown collection", "The collection does not exist in this workspace.");
        }

        if (SlugOrProblem(request.Slug, request.Title) is not { } slug)
        {
            return Problem422("Unusable slug", "The title and slug contain no usable characters.");
        }

        if (await db.Articles.AnyAsync(a =>
                a.WorkspaceId == article.WorkspaceId && a.Slug == slug && a.Id != id))
        {
            return SlugConflict(slug);
        }

        article.CollectionId = request.CollectionId;
        article.Title = request.Title;
        article.Slug = slug;
        article.Body = request.Body;
        article.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return article.ToResponse();
    }

    [HttpDelete("api/articles/{id:guid}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteArticle(Guid id)
    {
        var article = await FindArticleAsync(id);
        if (article is null)
        {
            return NotFound();
        }

        db.Articles.Remove(article);
        await db.SaveChangesAsync();

        return NoContent();
    }

    [HttpPost("api/articles/{id:guid}/publish")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ArticleResponse>> PublishArticle(Guid id)
    {
        var article = await FindArticleAsync(id);
        if (article is null)
        {
            return NotFound();
        }

        article.Publish(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        return article.ToResponse();
    }

    [HttpPost("api/articles/{id:guid}/unpublish")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ArticleResponse>> UnpublishArticle(Guid id)
    {
        var article = await FindArticleAsync(id);
        if (article is null)
        {
            return NotFound();
        }

        article.Unpublish(DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        return article.ToResponse();
    }

    /// <summary>
    /// Published articles matching the conversation's own words — its subject
    /// and the replies on it. Internal notes are excluded: they are staff
    /// shorthand, not the customer's problem statement.
    /// </summary>
    [HttpGet("api/conversations/{id:guid}/suggested-articles")]
    public async Task<ActionResult<List<SuggestedArticleResponse>>> SuggestArticles(
        Guid id, [FromQuery] int limit = 5)
    {
        var conversation = await db.Conversations
            .Include(c => c.Messages)
            .SingleOrDefaultAsync(c => c.Id == id && c.WorkspaceId == User.GetWorkspaceId());
        if (conversation is null)
        {
            return NotFound();
        }

        var texts = new List<string?> { conversation.Subject };
        texts.AddRange(conversation.Messages
            .Where(m => m.Kind == MessageKind.Reply)
            .Select(m => m.Body));

        var keywords = Keywords.Extract(texts);
        var articles = await db.Articles
            .Where(a => a.WorkspaceId == conversation.WorkspaceId
                && a.Status == ArticleStatus.Published)
            .ToListAsync();

        return ArticleSuggestions.Rank(articles, keywords, Math.Clamp(limit, 1, 25))
            .Select(m => m.ToResponse())
            .ToList();
    }

    private Task<Article?> FindArticleAsync(Guid id) =>
        db.Articles.SingleOrDefaultAsync(a => a.Id == id && a.WorkspaceId == User.GetWorkspaceId());

    /// <summary>The explicit slug when given, otherwise one derived from the name.</summary>
    private static string? SlugOrProblem(string? explicitSlug, string fallbackSource) =>
        Slugs.From(explicitSlug) ?? Slugs.From(fallbackSource);

    private ObjectResult SlugConflict(string slug) =>
        Conflict(new ProblemDetails
        {
            Title = "Duplicate slug",
            Detail = $"'{slug}' is already used in this workspace.",
            Status = StatusCodes.Status409Conflict,
        });

    private ObjectResult Problem422(string title, string detail) =>
        UnprocessableEntity(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status422UnprocessableEntity,
        });
}
