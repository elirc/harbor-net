using Harbor.Domain.Entities;

namespace Harbor.Domain;

/// <summary>An article matched against some text, with the reason it matched.</summary>
public record ArticleMatch(Article Article, int Score, IReadOnlyList<string> MatchedKeywords);

/// <summary>
/// Ranks help-center articles against the keywords of a conversation.
/// </summary>
public static class ArticleSuggestions
{
    /// <summary>A title hit is worth this many body hits.</summary>
    public const int TitleWeight = 3;

    public const int BodyWeight = 1;

    /// <summary>
    /// Scores articles by how many of the given keywords they share, weighting
    /// title matches above body matches, and returns the best ones.
    ///
    /// Drafts are filtered out here rather than by the caller: a suggestion is
    /// an invitation to send the article to a customer, and a customer cannot
    /// read a draft. Keeping the rule in one place means no endpoint can
    /// forget it.
    /// </summary>
    public static List<ArticleMatch> Rank(
        IEnumerable<Article> articles, IReadOnlySet<string> keywords, int limit)
    {
        if (keywords.Count == 0 || limit <= 0)
        {
            return [];
        }

        return articles
            .Where(a => a.Status == ArticleStatus.Published)
            .Select(article =>
            {
                var titleHits = Keywords.Extract(article.Title).Where(keywords.Contains).ToList();
                var bodyHits = Keywords.Extract(article.Body).Where(keywords.Contains).ToList();
                var score = (titleHits.Count * TitleWeight) + (bodyHits.Count * BodyWeight);

                return new ArticleMatch(
                    article, score,
                    titleHits.Union(bodyHits).OrderBy(k => k, StringComparer.Ordinal).ToList());
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Article.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }
}
