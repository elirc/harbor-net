using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Tests.Integration;

/// <summary>Pagination, the health probe, request ids, and concurrency tokens.</summary>
public class ProductionReadinessTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private static int Header(HttpResponseMessage response, string name) =>
        int.Parse(response.Headers.GetValues(name).Single());

    private async Task<WorkspaceResponse> WorkspaceWithTagsAsync(int count)
    {
        var workspace = await CreateWorkspaceAsync();
        foreach (var i in Enumerable.Range(0, count))
        {
            await CreateTagAsync(workspace.Id, $"tag-{i:D3}");
        }

        return workspace;
    }

    // --- Pagination ------------------------------------------------------

    [Fact]
    public async Task List_ReportsPagingHeaders()
    {
        var workspace = await WorkspaceWithTagsAsync(3);

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags");

        Assert.Equal(3, Header(response, Paging.TotalCountHeader));
        Assert.Equal(1, Header(response, Paging.PageHeader));
        Assert.Equal(Paging.DefaultPageSize, Header(response, Paging.PageSizeHeader));
        Assert.Equal(1, Header(response, Paging.TotalPagesHeader));
    }

    [Fact]
    public async Task List_WithNoPageRequested_ReturnsTheFirstPage_NotEverything()
    {
        // More rows than a page holds, with no paging parameters at all: the
        // point of the sprint is that this can no longer return everything.
        var workspace = await WorkspaceWithTagsAsync(Paging.DefaultPageSize + 5);

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags");
        var tags = await ReadAsync<List<TagResponse>>(response);

        Assert.Equal(Paging.DefaultPageSize, tags.Count);
        Assert.Equal(Paging.DefaultPageSize + 5, Header(response, Paging.TotalCountHeader));
        Assert.Equal(2, Header(response, Paging.TotalPagesHeader));
    }

    [Fact]
    public async Task List_WalksPages()
    {
        var workspace = await WorkspaceWithTagsAsync(5);

        var first = await ReadAsync<List<TagResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?page=1&pageSize=2"));
        var second = await ReadAsync<List<TagResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?page=2&pageSize=2"));
        var third = await ReadAsync<List<TagResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?page=3&pageSize=2"));

        Assert.Equal(["tag-000", "tag-001"], first.Select(t => t.Name));
        Assert.Equal(["tag-002", "tag-003"], second.Select(t => t.Name));
        Assert.Equal(["tag-004"], third.Select(t => t.Name));
    }

    [Fact]
    public async Task List_PastTheEnd_IsAnEmptyPage_NotAnError()
    {
        var workspace = await WorkspaceWithTagsAsync(2);

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?page=99");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await ReadAsync<List<TagResponse>>(response));
        Assert.Equal(2, Header(response, Paging.TotalCountHeader));
    }

    [Fact]
    public async Task List_ClampsAbsurdPaging()
    {
        var workspace = await WorkspaceWithTagsAsync(2);

        var oversized = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?pageSize=100000");
        var negative = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?page=-5&pageSize=0");

        Assert.Equal(Paging.MaxPageSize, Header(oversized, Paging.PageSizeHeader));
        Assert.Equal(1, Header(negative, Paging.PageHeader));
        Assert.Equal(1, Header(negative, Paging.PageSizeHeader));
    }

    [Fact]
    public async Task Pagination_ComposesWithConversationFilters()
    {
        var workspace = await CreateWorkspaceAsync();
        var support = await CreateInboxAsync(workspace.Id, "Support");
        var sales = await CreateInboxAsync(workspace.Id, "Sales");
        var contact = await CreateContactAsync(workspace.Id);
        foreach (var i in Enumerable.Range(0, 3))
        {
            await StartConversationAsync(workspace.Id, support.Id, contact.Id, $"support {i}");
        }

        await StartConversationAsync(workspace.Id, sales.Id, contact.Id, "sales");

        var response = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/conversations?inboxId={support.Id}&pageSize=2");
        var page = await ReadAsync<List<ConversationSummaryResponse>>(response);

        // The total counts the filtered set, not the whole workspace.
        Assert.Equal(3, Header(response, Paging.TotalCountHeader));
        Assert.Equal(2, page.Count);
    }

    [Fact]
    public async Task Pagination_AppliesToPublicEndpointsToo()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await ReadAsync<CollectionResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/collections",
                new CreateCollectionRequest("Guides"), Json));
        foreach (var i in Enumerable.Range(0, 3))
        {
            var article = await ReadAsync<ArticleResponse>(
                await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/articles",
                    new CreateArticleRequest(collection.Id, $"Guide {i}", "Body"), Json));
            await Client.PostAsync($"/api/articles/{article.Id}/publish", null);
        }

        using var anonymous = Factory.CreateClient();
        var response = await anonymous.GetAsync(
            $"/api/public/workspaces/{workspace.Id}/articles?pageSize=2");

        Assert.Equal(3, Header(response, Paging.TotalCountHeader));
        Assert.Equal(2, (await ReadAsync<List<PublicArticleResponse>>(response)).Count);
    }

    // --- Health ----------------------------------------------------------

    [Fact]
    public async Task Health_ProbesTheDatabase()
    {
        using var anonymous = Factory.CreateClient();

        var response = await anonymous.GetAsync("/health");
        var body = await ReadAsync<HealthPayload>(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", body.Status);
        Assert.Equal("harbor-net", body.Name);
        Assert.True(body.Database.Healthy);
        Assert.Null(body.Database.Error);
        Assert.True(body.Database.DurationMs >= 0);
    }

    [Fact]
    public async Task Health_NeedsNoApiKey()
    {
        using var anonymous = Factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
    }

    // --- Request logging -------------------------------------------------

    [Fact]
    public async Task EveryResponse_CarriesARequestId()
    {
        using var anonymous = Factory.CreateClient();

        var response = await anonymous.GetAsync("/health");

        var requestId = response.Headers.GetValues(RequestLoggingMiddleware.RequestIdHeader).Single();
        Assert.False(string.IsNullOrWhiteSpace(requestId));
    }

    [Fact]
    public async Task AnUpstreamRequestId_IsEchoedBack()
    {
        using var anonymous = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add(RequestLoggingMiddleware.RequestIdHeader, "trace-from-the-proxy");

        var response = await anonymous.SendAsync(request);

        // A trace has to survive the hop, or correlating logs is guesswork.
        Assert.Equal(
            "trace-from-the-proxy",
            response.Headers.GetValues(RequestLoggingMiddleware.RequestIdHeader).Single());
    }

    // --- Concurrency -----------------------------------------------------

    [Fact]
    public async Task Conversation_VersionChangesOnEveryUpdate()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var before = VersionOf(convo.Id);
        await SetPriorityAsync(convo.Id, Domain.ConversationPriority.Urgent);
        var after = VersionOf(convo.Id);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task Conversation_StaleWriteLosesTheRace()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Factory.WithDb(mine =>
        {
            var read = mine.Conversations.Single(c => c.Id == convo.Id);

            // Someone else edits and commits while I am still holding my copy.
            Factory.WithDb(theirs =>
            {
                var other = theirs.Conversations.Single(c => c.Id == convo.Id);
                other.Subject = "Changed by the other agent";
                theirs.SaveChanges();
            });

            read.Subject = "Changed by me";

            // My UPDATE matches no row, because the token moved underneath it.
            Assert.Throws<DbUpdateConcurrencyException>(() => mine.SaveChanges());
        });

        // The first writer's change is the one that survives.
        Factory.WithDb(db =>
            Assert.Equal(
                "Changed by the other agent",
                db.Conversations.Single(c => c.Id == convo.Id).Subject));
    }

    [Fact]
    public async Task WebhookDelivery_StaleWriteLosesTheRace()
    {
        var workspace = await CreateWorkspaceAsync();
        await ReadAsync<WebhookCreatedResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/webhooks",
                new CreateWebhookRequest("https://example.test/hooks",
                    [Domain.WebhookEventType.ConversationCreated]), Json));
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Factory.WithDb(mine =>
        {
            var read = mine.WebhookDeliveries.First(d => d.WorkspaceId == workspace.Id);

            Factory.WithDb(theirs =>
            {
                var other = theirs.WebhookDeliveries.First(d => d.WorkspaceId == workspace.Id);
                other.Succeed(200, DateTimeOffset.UtcNow);
                theirs.SaveChanges();
            });

            // Two dispatchers must not both claim the same delivery.
            read.Succeed(200, DateTimeOffset.UtcNow);
            Assert.Throws<DbUpdateConcurrencyException>(() => mine.SaveChanges());
        });
    }

    [Fact]
    public async Task Conversation_SequentialUpdates_DoNotConflict()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        // Ordinary back-to-back API calls each re-read, so the token never
        // gets in the way of normal work.
        await SetPriorityAsync(convo.Id, Domain.ConversationPriority.High);
        await SetPriorityAsync(convo.Id, Domain.ConversationPriority.Urgent);
        var closed = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(Domain.ConversationState.Closed, null), Json);

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }

    private Guid VersionOf(Guid conversationId)
    {
        var version = Guid.Empty;
        Factory.WithDb(db =>
            version = db.Conversations.AsNoTracking().Single(c => c.Id == conversationId).Version);
        return version;
    }

    private sealed record DatabasePayload(bool Healthy, long DurationMs, string? Error);

    private sealed record HealthPayload(
        string Status, string Name, string Version, DateTimeOffset UtcNow, DatabasePayload Database);
}
