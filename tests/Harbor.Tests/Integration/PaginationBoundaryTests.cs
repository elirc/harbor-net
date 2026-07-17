using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harbor.Api.Contracts;
using Harbor.Api.Infrastructure;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>
/// Pagination at its edges, and the binding regression that made it a no-op.
///
/// Binding a complex <c>[FromQuery] PageRequest page</c> collided with the
/// object's own <c>Page</c> property: the model binder matched the prefix
/// rather than the value, every list silently ignored <c>?page=</c>, and every
/// test still passed because page one is what they all asserted. Renaming the
/// parameter to <c>paging</c> fixed it. The sweep below is the guard: it walks
/// to a second page on every list endpoint, so re-introducing the collision on
/// any one of them fails here.
/// </summary>
public class PaginationBoundaryTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private static int Header(HttpResponseMessage response, string name) =>
        int.Parse(response.Headers.GetValues(name).Single());

    /// <summary>
    /// Every workspace-scoped list endpoint, with a factory that puts three
    /// distinct rows behind it. Three is enough for a real second page.
    /// </summary>
    public static TheoryData<string> PaginatedListEndpoints() =>
    [
        "tags",
        "contacts",
        "teammates",
        "teams",
        "canned-replies",
        "sla-policies",
        "segments",
        "inboxes",
        "conversations",
        "collections",
        "articles",
        "webhooks",
    ];

    /// <summary>Creates three rows behind the named list endpoint.</summary>
    private async Task SeedThreeAsync(Guid workspaceId, string endpoint)
    {
        switch (endpoint)
        {
            case "tags":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await CreateTagAsync(workspaceId, $"tag-{i}");
                }

                break;
            case "contacts":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await CreateContactAsync(workspaceId, $"Contact {i}");
                }

                break;
            case "teammates":
                // The bootstrap admin already counts as one.
                foreach (var i in Enumerable.Range(0, 2))
                {
                    await CreateTeammateAsync(workspaceId, $"Agent {i}");
                }

                break;
            case "teams":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await CreateTeamAsync(workspaceId, $"Team {i}");
                }

                break;
            case "canned-replies":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/canned-replies",
                        new CreateCannedReplyRequest($"sc{i}", $"Title {i}", "Body"), Json);
                }

                break;
            case "sla-policies":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    var inbox = await CreateInboxAsync(workspaceId, $"Inbox {i}");
                    await CreateSlaPolicyAsync(workspaceId, $"Policy {i}", inboxId: inbox.Id);
                }

                break;
            case "segments":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/segments",
                        new CreateSegmentRequest($"Segment {i}",
                            new SegmentRuleSet(SegmentMatch.All,
                                [new SegmentCondition("email", SegmentOperator.Exists)])),
                        Json);
                }

                break;
            case "inboxes":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await CreateInboxAsync(workspaceId, $"Inbox {i}");
                }

                break;
            case "conversations":
                var conversationInbox = await CreateInboxAsync(workspaceId);
                var contact = await CreateContactAsync(workspaceId);
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await StartConversationAsync(workspaceId, conversationInbox.Id, contact.Id, $"c{i}");
                }

                break;
            case "collections":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/collections",
                        new CreateCollectionRequest($"Collection {i}"), Json);
                }

                break;
            case "articles":
                var collection = await ReadAsync<CollectionResponse>(
                    await Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/collections",
                        new CreateCollectionRequest("Guides"), Json));
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/articles",
                        new CreateArticleRequest(collection.Id, $"Article {i}", "Body"), Json);
                }

                break;
            case "webhooks":
                foreach (var i in Enumerable.Range(0, 3))
                {
                    await Client.PostAsJsonAsync($"/api/workspaces/{workspaceId}/webhooks",
                        new CreateWebhookRequest($"https://example.test/hook-{i}",
                            [WebhookEventType.ConversationCreated]), Json);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint, "Unseeded endpoint.");
        }
    }

    // --- The binding regression -------------------------------------------

    [Theory]
    [MemberData(nameof(PaginatedListEndpoints))]
    public async Task EveryListEndpoint_HonoursThePageParameter(string endpoint)
    {
        var workspace = await CreateWorkspaceAsync();
        await SeedThreeAsync(workspace.Id, endpoint);

        var first = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/{endpoint}?page=1&pageSize=1");
        var second = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/{endpoint}?page=2&pageSize=1");

        // If ?page= were ignored again, both pages would report page 1 and hand
        // back the same row — which is exactly what the old bug did.
        Assert.Equal(1, Header(first, Paging.PageHeader));
        Assert.Equal(2, Header(second, Paging.PageHeader));
        Assert.Equal(1, Header(second, Paging.PageSizeHeader));
        Assert.Equal(3, Header(second, Paging.TotalCountHeader));
        Assert.Equal(3, Header(second, Paging.TotalPagesHeader));

        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();
        Assert.NotEqual(firstBody, secondBody);
    }

    [Theory]
    [MemberData(nameof(PaginatedListEndpoints))]
    public async Task EveryListEndpoint_HonoursPageSize(string endpoint)
    {
        var workspace = await CreateWorkspaceAsync();
        await SeedThreeAsync(workspace.Id, endpoint);

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/{endpoint}?pageSize=2");

        Assert.Equal(2, Header(response, Paging.PageSizeHeader));
        Assert.Equal(3, Header(response, Paging.TotalCountHeader));
        Assert.Equal(2, Header(response, Paging.TotalPagesHeader));
    }

    [Fact]
    public async Task PagingBindsAlongsideFilters_OnTheConversationList()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        foreach (var i in Enumerable.Range(0, 3))
        {
            await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, $"open {i}");
        }

        // The filter record inherits PageRequest, so the same binding has to
        // work when the parameter is carrying filters too.
        var response = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/conversations?state=Open&page=2&pageSize=1");

        Assert.Equal(2, Header(response, Paging.PageHeader));
        Assert.Equal(3, Header(response, Paging.TotalCountHeader));
        Assert.Single(await ReadAsync<List<ConversationSummaryResponse>>(response));
    }

    [Fact]
    public async Task PagingBindsOnResourceScopedLists()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        foreach (var name in new[] { "One", "Two", "Three" })
        {
            var teammate = await CreateTeammateAsync(workspace.Id, name);
            await AssignAsync(convo.Id, teammate.Id);
        }

        var response = await Client.GetAsync(
            $"/api/conversations/{convo.Id}/assignment-events?page=2&pageSize=1");

        Assert.Equal(2, Header(response, Paging.PageHeader));
        Assert.Equal(3, Header(response, Paging.TotalCountHeader));
        Assert.Single(await ReadAsync<List<AssignmentEventResponse>>(response));
    }

    [Fact]
    public async Task WalkingEveryPage_VisitsEveryRowExactlyOnce()
    {
        var workspace = await CreateWorkspaceAsync();
        foreach (var i in Enumerable.Range(0, 7))
        {
            await CreateTagAsync(workspace.Id, $"tag-{i:D2}");
        }

        var seen = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            seen.AddRange((await ReadAsync<List<TagResponse>>(
                    await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?page={page}&pageSize=2")))
                .Select(t => t.Name));
        }

        // No row skipped at a boundary, none handed out twice.
        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    // --- Edges --------------------------------------------------------------

    [Fact]
    public async Task TheLastPage_MayBeShort()
    {
        var workspace = await CreateWorkspaceAsync();
        foreach (var i in Enumerable.Range(0, 5))
        {
            await CreateTagAsync(workspace.Id, $"tag-{i}");
        }

        var last = await ReadAsync<List<TagResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags?page=3&pageSize=2"));

        Assert.Single(last);
    }

    [Fact]
    public async Task AnEmptyCollection_ReportsOneTotalPage_NotZero()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags");

        // A client looping "while page <= totalPages" must terminate, and must
        // not be told page 1 does not exist.
        Assert.Equal(0, Header(response, Paging.TotalCountHeader));
        Assert.Equal(1, Header(response, Paging.TotalPagesHeader));
        Assert.Empty(await ReadAsync<List<TagResponse>>(response));
    }

    [Theory]
    [InlineData("?page=0", 1, Paging.DefaultPageSize)]
    [InlineData("?page=-1", 1, Paging.DefaultPageSize)]
    [InlineData("?pageSize=0", 1, 1)]
    [InlineData("?pageSize=-10", 1, 1)]
    [InlineData("?pageSize=999999", 1, Paging.MaxPageSize)]
    [InlineData("?page=1&pageSize=201", 1, Paging.MaxPageSize)]
    [InlineData("?page=1&pageSize=200", 1, Paging.MaxPageSize)]
    public async Task AbsurdPaging_IsClamped_NotRejected(string query, int page, int pageSize)
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTagAsync(workspace.Id, "only");

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags{query}");

        // Clamping beats 400: a client walking pages should get an empty page
        // at the end, not an error to special-case.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(page, Header(response, Paging.PageHeader));
        Assert.Equal(pageSize, Header(response, Paging.PageSizeHeader));
    }

    [Theory]
    [InlineData("?page=abc")]
    [InlineData("?pageSize=abc")]
    [InlineData("?page=1.5")]
    [InlineData("?page=9999999999999999999")]
    public async Task UnparseablePaging_Returns400_RatherThanSilentlyDefaulting(string query)
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags{query}");

        // Nonsense that cannot be clamped is a client bug worth reporting.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PagingHeaders_AreOnEveryListResponse_IncludingEmptyAndAnonymousOnes()
    {
        var workspace = await CreateWorkspaceAsync();
        using var anonymous = Factory.CreateClient();

        var authenticated = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags");
        var publicList = await anonymous.GetAsync(
            $"/api/public/workspaces/{workspace.Id}/articles");

        foreach (var response in new[] { authenticated, publicList })
        {
            Assert.True(response.Headers.Contains(Paging.TotalCountHeader));
            Assert.True(response.Headers.Contains(Paging.PageHeader));
            Assert.True(response.Headers.Contains(Paging.PageSizeHeader));
            Assert.True(response.Headers.Contains(Paging.TotalPagesHeader));
        }
    }

    [Fact]
    public async Task TheBodyStaysAPlainArray_WithTotalsInHeaders()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTagAsync(workspace.Id, "billing");

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags");
        var body = await response.Content.ReadAsStringAsync();

        // The envelope lives in the headers on purpose: bodies stay arrays, so
        // no client had to change when pagination arrived.
        Assert.StartsWith("[", body.TrimStart());
        Assert.DoesNotContain("totalCount", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADefaultPage_IsBounded_EvenWithNoParameters()
    {
        var workspace = await CreateWorkspaceAsync();
        foreach (var i in Enumerable.Range(0, Paging.DefaultPageSize + 3))
        {
            await CreateTagAsync(workspace.Id, $"tag-{i:D3}");
        }

        var tags = await ReadAsync<List<TagResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/tags"));

        // The property that matters in production: no list endpoint can return
        // an unbounded number of rows just because the caller said nothing.
        Assert.Equal(Paging.DefaultPageSize, tags.Count);
    }

    // --- Malformed input ----------------------------------------------------

    [Fact]
    public async Task AMalformedFilterValue_Returns400()
    {
        var workspace = await CreateWorkspaceAsync();

        var badState = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/conversations?state=Exploded");
        var badGuid = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/conversations?inboxId=not-a-guid");

        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badGuid.StatusCode);
    }

    [Fact]
    public async Task AnUnknownQueryParameter_IsIgnored_NotAnError()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTagAsync(workspace.Id, "billing");

        var response = await Client.GetAsync(
            $"/api/workspaces/{workspace.Id}/tags?colour=blue&page=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await ReadAsync<List<TagResponse>>(response));
    }

    [Fact]
    public async Task AnEmptyBody_OnAWriteEndpoint_Returns400_NotAServerError()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsync(
            $"/api/workspaces/{workspace.Id}/tags",
            new StringContent("", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
