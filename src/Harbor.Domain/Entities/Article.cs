namespace Harbor.Domain.Entities;

/// <summary>A group of help-center articles.</summary>
public class ArticleCollection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Name { get; set; }

    /// <summary>URL-safe identifier, unique within the workspace.</summary>
    public required string Slug { get; set; }

    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public ICollection<Article> Articles { get; } = new List<Article>();
}

public class Article
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid CollectionId { get; set; }
    public required string Title { get; set; }

    /// <summary>URL-safe identifier, unique within the workspace.</summary>
    public required string Slug { get; set; }

    public required string Body { get; set; }
    public ArticleStatus Status { get; set; } = ArticleStatus.Draft;

    /// <summary>When the article was first published; kept across unpublishing.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public ArticleCollection? Collection { get; set; }

    public void Publish(DateTimeOffset now)
    {
        Status = ArticleStatus.Published;
        PublishedAt ??= now;
        UpdatedAt = now;
    }

    /// <summary>
    /// Pulls the article back to draft. PublishedAt is deliberately kept: it
    /// records when the article first went live, which unpublishing does not
    /// undo, and republishing should not reset.
    /// </summary>
    public void Unpublish(DateTimeOffset now)
    {
        Status = ArticleStatus.Draft;
        UpdatedAt = now;
    }
}
