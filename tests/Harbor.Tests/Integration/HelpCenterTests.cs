using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>Collections, article drafts/publishing, public reads, and suggestions.</summary>
public class HelpCenterTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<CollectionResponse> CreateCollectionAsync(
        Guid workspaceId, string name = "Getting started", string? slug = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/collections",
            new CreateCollectionRequest(name, "Basics", slug), Json);
        return await ReadAsync<CollectionResponse>(response);
    }

    private async Task<ArticleResponse> CreateArticleAsync(
        Guid workspaceId, Guid collectionId, string title = "Resetting your password",
        string body = "Use forgot password on the sign-in page.", string? slug = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/articles",
            new CreateArticleRequest(collectionId, title, body, slug), Json);
        return await ReadAsync<ArticleResponse>(response);
    }

    private async Task<ArticleResponse> PublishAsync(Guid articleId) =>
        await ReadAsync<ArticleResponse>(
            await Client.PostAsync($"/api/articles/{articleId}/publish", null));

    /// <summary>A client with no API key at all, for the public endpoints.</summary>
    private HttpClient Anonymous() => Factory.CreateClient();

    [Fact]
    public async Task Article_IsCreatedAsADraft_WithASlugFromItsTitle()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);

        var article = await CreateArticleAsync(workspace.Id, collection.Id);

        Assert.Equal(ArticleStatus.Draft, article.Status);
        Assert.Null(article.PublishedAt);
        Assert.Equal("resetting-your-password", article.Slug);
    }

    [Fact]
    public async Task Collection_SlugIsDerived_OrExplicit()
    {
        var workspace = await CreateWorkspaceAsync();

        var derived = await CreateCollectionAsync(workspace.Id, "Getting Started!");
        var explicitly = await CreateCollectionAsync(workspace.Id, "Billing", "money-stuff");

        Assert.Equal("getting-started", derived.Slug);
        Assert.Equal("money-stuff", explicitly.Slug);
    }

    [Fact]
    public async Task Publish_ThenUnpublish_KeepsPublishedAt()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var article = await CreateArticleAsync(workspace.Id, collection.Id);

        var published = await PublishAsync(article.Id);
        var unpublished = await ReadAsync<ArticleResponse>(
            await Client.PostAsync($"/api/articles/{article.Id}/unpublish", null));

        Assert.Equal(ArticleStatus.Published, published.Status);
        Assert.NotNull(published.PublishedAt);
        Assert.Equal(ArticleStatus.Draft, unpublished.Status);
        // The first-published moment is history and survives.
        Assert.Equal(published.PublishedAt, unpublished.PublishedAt);
    }

    [Fact]
    public async Task Republishing_DoesNotResetPublishedAt()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var article = await CreateArticleAsync(workspace.Id, collection.Id);
        var first = await PublishAsync(article.Id);

        await Client.PostAsync($"/api/articles/{article.Id}/unpublish", null);
        var again = await PublishAsync(article.Id);

        Assert.Equal(first.PublishedAt, again.PublishedAt);
    }

    [Fact]
    public async Task Public_ReadsPublishedArticles_WithoutAnApiKey()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var article = await CreateArticleAsync(workspace.Id, collection.Id);
        await PublishAsync(article.Id);

        using var anonymous = Anonymous();
        var articles = await ReadAsync<List<PublicArticleResponse>>(
            await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/articles"));
        var bySlug = await ReadAsync<PublicArticleResponse>(
            await anonymous.GetAsync(
                $"/api/public/workspaces/{workspace.Id}/articles/{article.Slug}"));

        Assert.Equal(article.Id, Assert.Single(articles).Id);
        Assert.Equal("Resetting your password", bySlug.Title);
    }

    [Fact]
    public async Task Public_NeverExposesDrafts()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var draft = await CreateArticleAsync(workspace.Id, collection.Id);

        using var anonymous = Anonymous();
        var listed = await ReadAsync<List<PublicArticleResponse>>(
            await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/articles"));
        var bySlug = await anonymous.GetAsync(
            $"/api/public/workspaces/{workspace.Id}/articles/{draft.Slug}");
        var searched = await ReadAsync<List<PublicArticleResponse>>(
            await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/articles?q=password"));

        Assert.Empty(listed);
        Assert.Empty(searched);
        // A draft's slug is indistinguishable from one that never existed.
        Assert.Equal(HttpStatusCode.NotFound, bySlug.StatusCode);
    }

    [Fact]
    public async Task Public_UnpublishingHidesAnArticleAgain()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var article = await CreateArticleAsync(workspace.Id, collection.Id);
        await PublishAsync(article.Id);
        await Client.PostAsync($"/api/articles/{article.Id}/unpublish", null);

        using var anonymous = Anonymous();
        var response = await anonymous.GetAsync(
            $"/api/public/workspaces/{workspace.Id}/articles/{article.Slug}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Public_ListsOnlyCollectionsWithPublishedArticles()
    {
        var workspace = await CreateWorkspaceAsync();
        var live = await CreateCollectionAsync(workspace.Id, "Live");
        var empty = await CreateCollectionAsync(workspace.Id, "Work in progress");
        var article = await CreateArticleAsync(workspace.Id, live.Id);
        await PublishAsync(article.Id);
        await CreateArticleAsync(workspace.Id, empty.Id, "Secret plans", "Not ready.");

        using var anonymous = Anonymous();
        var collections = await ReadAsync<List<CollectionResponse>>(
            await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/collections"));

        var only = Assert.Single(collections);
        Assert.Equal("Live", only.Name);
        Assert.Equal(1, only.PublishedArticles);
    }

    [Fact]
    public async Task Public_ReadableByATeammateOfAnotherWorkspace()
    {
        var host = await CreateWorkspaceAsync("Host");
        var collection = await CreateCollectionAsync(host.Id);
        var article = await CreateArticleAsync(host.Id, collection.Id);
        await PublishAsync(article.Id);

        // A signed-in teammate of a different workspace: the public help
        // center is public, so their key must not turn a read into a 403.
        await CreateWorkspaceAsync("Other");
        var response = await Client.GetAsync($"/api/public/workspaces/{host.Id}/articles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await ReadAsync<List<PublicArticleResponse>>(response));
    }

    [Fact]
    public async Task Public_SearchMatchesTitleAndBody()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var password = await CreateArticleAsync(workspace.Id, collection.Id);
        var export = await CreateArticleAsync(
            workspace.Id, collection.Id, "Exporting data", "Use Settings then Export.");
        await PublishAsync(password.Id);
        await PublishAsync(export.Id);

        using var anonymous = Anonymous();
        var byTitle = await ReadAsync<List<PublicArticleResponse>>(
            await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/articles?q=exporting"));
        var byBody = await ReadAsync<List<PublicArticleResponse>>(
            await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/articles?q=SIGN-IN"));

        Assert.Equal(export.Id, Assert.Single(byTitle).Id);
        Assert.Equal(password.Id, Assert.Single(byBody).Id);
    }

    [Fact]
    public async Task Public_ForUnknownWorkspace_Returns404()
    {
        using var anonymous = Anonymous();

        var response = await anonymous.GetAsync(
            $"/api/public/workspaces/{Guid.NewGuid()}/articles");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_ListIncludesDrafts_AndFilters()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var draft = await CreateArticleAsync(workspace.Id, collection.Id);
        var live = await CreateArticleAsync(
            workspace.Id, collection.Id, "Exporting data", "Use Settings then Export.");
        await PublishAsync(live.Id);

        var all = await ReadAsync<List<ArticleResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/articles"));
        var drafts = await ReadAsync<List<ArticleResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/articles?status=Draft"));
        var searched = await ReadAsync<List<ArticleResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/articles?q=export"));

        Assert.Equal(2, all.Count);
        Assert.Equal(draft.Id, Assert.Single(drafts).Id);
        Assert.Equal(live.Id, Assert.Single(searched).Id);
    }

    [Fact]
    public async Task SuggestedArticles_MatchTheConversationsWords()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var password = await CreateArticleAsync(workspace.Id, collection.Id);
        var shipping = await CreateArticleAsync(
            workspace.Id, collection.Id, "Shipping times", "We ship within three days.");
        await PublishAsync(password.Id);
        await PublishAsync(shipping.Id);

        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id,
            "Password will not reset", "My password reset link never arrives.");

        var suggestions = await ReadAsync<List<SuggestedArticleResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/suggested-articles"));

        var top = Assert.Single(suggestions);
        Assert.Equal(password.Id, top.Id);
        Assert.Contains("password", top.MatchedKeywords);
        Assert.True(top.Score > 0);
    }

    [Fact]
    public async Task SuggestedArticles_NeverIncludeDrafts()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        await CreateArticleAsync(workspace.Id, collection.Id);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id, "Password reset", "My password will not reset.");

        var suggestions = await ReadAsync<List<SuggestedArticleResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/suggested-articles"));

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task SuggestedArticles_IgnoreInternalNotes()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var shipping = await CreateArticleAsync(
            workspace.Id, collection.Id, "Shipping times", "We ship within three days.");
        await PublishAsync(shipping.Id);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var convo = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id, "Billing question", "I was charged twice.");

        // A note mentioning shipping must not drag the shipping article in:
        // notes are staff shorthand, not the customer's problem.
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Note,
                "Check shipping records for this order."),
            Json);
        var suggestions = await ReadAsync<List<SuggestedArticleResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/suggested-articles"));

        Assert.Empty(suggestions);
    }

    [Fact]
    public async Task SuggestedArticles_RespectTheLimit()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        foreach (var i in Enumerable.Range(0, 4))
        {
            var article = await CreateArticleAsync(
                workspace.Id, collection.Id, $"Password guide {i}", "Password reset advice.");
            await PublishAsync(article.Id);
        }

        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id, "Password", "My password needs a reset.");

        var suggestions = await ReadAsync<List<SuggestedArticleResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/suggested-articles?limit=2"));

        Assert.Equal(2, suggestions.Count);
    }

    [Fact]
    public async Task SuggestedArticles_ForForeignConversation_Returns404()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await CreateWorkspaceAsync("Other");

        var response = await Client.GetAsync($"/api/conversations/{convo.Id}/suggested-articles");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DuplicateSlug_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        await CreateArticleAsync(workspace.Id, collection.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/articles",
            new CreateArticleRequest(collection.Id, "Resetting your password", "Duplicate."), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UnusableSlug_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/articles",
            new CreateArticleRequest(collection.Id, "!!!", "Body"), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Article_InAForeignCollection_Returns422()
    {
        var other = await CreateWorkspaceAsync("Other");
        var foreignCollection = await CreateCollectionAsync(other.Id);
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/articles",
            new CreateArticleRequest(foreignCollection.Id, "Cross tenant", "Body"), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task DeletingANonEmptyCollection_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        await CreateArticleAsync(workspace.Id, collection.Id);

        var response = await Client.DeleteAsync($"/api/collections/{collection.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task EmptyCollection_CanBeDeleted()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);

        var response = await Client.DeleteAsync($"/api/collections/{collection.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Authoring_IsAdminOnly_ButReadingIsNot()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await CreateCollectionAsync(workspace.Id);
        var agent = await CreateTeammateAsync(workspace.Id);
        ActAs(agent.ApiKey);

        var write = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/articles",
            new CreateArticleRequest(collection.Id, "Agent article", "Body"), Json);
        var read = await Client.GetAsync($"/api/workspaces/{workspace.Id}/articles");

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task Articles_ForForeignWorkspace_Return403()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/workspaces/{other.Id}/articles");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForeignArticle_Returns404()
    {
        var other = await CreateWorkspaceAsync("Other");
        var collection = await CreateCollectionAsync(other.Id);
        var article = await CreateArticleAsync(other.Id, collection.Id);
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/articles/{article.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
