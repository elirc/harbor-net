using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

public class KeywordsTests
{
    [Fact]
    public void Extract_LowercasesAndSplitsOnPunctuation()
    {
        var keywords = Keywords.Extract("Password reset — FAILED, again!");

        Assert.Contains("password", keywords);
        Assert.Contains("reset", keywords);
        Assert.Contains("failed", keywords);
    }

    [Fact]
    public void Extract_DropsStopWordsAndShortTokens()
    {
        var keywords = Keywords.Extract("I can not log in to my account and I need help");

        Assert.Contains("log", keywords);
        Assert.Contains("account", keywords);
        Assert.DoesNotContain("the", keywords);
        Assert.DoesNotContain("and", keywords);
        Assert.DoesNotContain("help", keywords);
        // Shorter than the minimum length.
        Assert.DoesNotContain("in", keywords);
        Assert.DoesNotContain("to", keywords);
    }

    [Fact]
    public void Extract_Deduplicates()
    {
        var keywords = Keywords.Extract("refund refund REFUND");

        Assert.Equal(["refund"], keywords);
    }

    [Fact]
    public void Extract_KeepsDigitsAndAlphanumerics()
    {
        var keywords = Keywords.Extract("Error 500 on plan v2ready");

        Assert.Contains("500", keywords);
        Assert.Contains("error", keywords);
        Assert.Contains("v2ready", keywords);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a I of")]
    public void Extract_OfNothingUseful_IsEmpty(string? text)
    {
        Assert.Empty(Keywords.Extract(text));
    }

    [Fact]
    public void Extract_OverManyTexts_IsTheUnion()
    {
        var keywords = Keywords.Extract(["password reset", "billing refund", null]);

        Assert.Contains("password", keywords);
        Assert.Contains("billing", keywords);
        Assert.Contains("refund", keywords);
    }
}

public class SlugsTests
{
    [Theory]
    [InlineData("Resetting your password", "resetting-your-password")]
    [InlineData("  Spaces   everywhere  ", "spaces-everywhere")]
    [InlineData("Punctuation!! Removed??", "punctuation-removed")]
    [InlineData("Already-slugged", "already-slugged")]
    [InlineData("Numbers 123 kept", "numbers-123-kept")]
    [InlineData("--leading and trailing--", "leading-and-trailing")]
    public void From_ProducesUrlSafeSlugs(string input, string expected)
    {
        Assert.Equal(expected, Slugs.From(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void From_WithNothingSluggable_IsNull(string? input)
    {
        Assert.Null(Slugs.From(input));
    }

    [Fact]
    public void From_TruncatesToMaxLength()
    {
        var slug = Slugs.From(new string('a', 300));

        Assert.Equal(200, slug!.Length);
    }
}

public class ArticleSuggestionTests
{
    private static readonly Guid Workspace = Guid.NewGuid();

    private static Article Article(
        string title, string body, ArticleStatus status = ArticleStatus.Published) => new()
        {
            WorkspaceId = Workspace,
            CollectionId = Guid.NewGuid(),
            Title = title,
            Slug = Slugs.From(title)!,
            Body = body,
            Status = status,
        };

    [Fact]
    public void Rank_ScoresTitleMatchesAboveBodyMatches()
    {
        var titleMatch = Article("Password reset", "Unrelated body text about widgets.");
        var bodyMatch = Article("Widgets", "Sometimes a password may need attention.");

        var ranked = ArticleSuggestions.Rank(
            [bodyMatch, titleMatch], Keywords.Extract("password"), 5);

        Assert.Equal("Password reset", ranked[0].Article.Title);
        Assert.Equal(ArticleSuggestions.TitleWeight, ranked[0].Score);
        Assert.Equal(ArticleSuggestions.BodyWeight, ranked[1].Score);
    }

    [Fact]
    public void Rank_NeverSuggestsDrafts()
    {
        var draft = Article("Password reset", "How to reset a password.", ArticleStatus.Draft);

        Assert.Empty(ArticleSuggestions.Rank([draft], Keywords.Extract("password reset"), 5));
    }

    [Fact]
    public void Rank_ExcludesArticlesWithNoOverlap()
    {
        var unrelated = Article("Shipping times", "We ship within three days.");

        Assert.Empty(ArticleSuggestions.Rank([unrelated], Keywords.Extract("password reset"), 5));
    }

    [Fact]
    public void Rank_ReportsTheMatchedKeywords()
    {
        var article = Article("Password reset", "Reset your password from the login page.");

        var match = Assert.Single(ArticleSuggestions.Rank(
            [article], Keywords.Extract("password reset failing"), 5));

        Assert.Contains("password", match.MatchedKeywords);
        Assert.Contains("reset", match.MatchedKeywords);
        Assert.DoesNotContain("failing", match.MatchedKeywords);
    }

    [Fact]
    public void Rank_MatchesWholeWords_NotSubstrings()
    {
        // "cat" must not match "category".
        var article = Article("Category management", "Organise your categories here.");

        Assert.Empty(ArticleSuggestions.Rank([article], Keywords.Extract("cat"), 5));
    }

    [Fact]
    public void Rank_RespectsTheLimit()
    {
        var articles = Enumerable.Range(0, 10)
            .Select(i => Article($"Password article {i}", "password"))
            .ToList();

        Assert.Equal(3, ArticleSuggestions.Rank(articles, Keywords.Extract("password"), 3).Count);
    }

    [Fact]
    public void Rank_WithNoKeywords_IsEmpty()
    {
        var article = Article("Password reset", "Reset it.");

        Assert.Empty(ArticleSuggestions.Rank([article], Keywords.Extract("the and a"), 5));
    }

    [Fact]
    public void Rank_SumsTitleAndBodyHits()
    {
        var article = Article("Password reset", "Reset your password here.");

        var match = Assert.Single(
            ArticleSuggestions.Rank([article], Keywords.Extract("password reset"), 5));

        // Both keywords hit the title and the body: 2*3 + 2*1.
        Assert.Equal(8, match.Score);
    }
}
