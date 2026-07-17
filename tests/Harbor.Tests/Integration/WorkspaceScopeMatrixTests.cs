using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harbor.Api.Contracts;
using Harbor.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Harbor.Tests.Integration;

/// <summary>
/// The tenancy half of the authorization matrix: workspace-scoped routes
/// against a foreign key, and the 403-vs-404 policy that decides which of the
/// two an outsider is told.
///
/// The rule being pinned: a *route* naming a workspace you do not belong to is
/// a 403, decided without touching the database — so it leaks nothing about
/// which workspaces exist. A *resource* you may not see is a 404, because the
/// lookup is scoped and a row you cannot reach simply is not there.
/// </summary>
public class WorkspaceScopeMatrixTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private static StringContent Body() => new("{}", Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(string method, string url) =>
        method == "GET" ? await Client.GetAsync(url) : await Client.PostAsync(url, Body());

    // --- Foreign workspace: always 403 -------------------------------------

    [Theory]
    [MemberData(nameof(AuthorizationMatrixTests.AgentReadableEndpoints),
        MemberType = typeof(AuthorizationMatrixTests))]
    public async Task EveryWorkspaceRoute_RejectsAForeignKey_With403(string path)
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();
        // Acting as our own admin, reaching into Other.

        var response = await Client.GetAsync($"/api/workspaces/{other.Id}/{path}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AuthorizationMatrixTests.AdminOnlyEndpoints),
        MemberType = typeof(AuthorizationMatrixTests))]
    public async Task WorkspaceScope_OutranksRole_ForForeignAdmins(string method, string path)
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();

        var response = await SendAsync(method, $"/api/workspaces/{other.Id}/{path}");

        // Being an Admin of somewhere else is not being an Admin here.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("inboxes")]
    [InlineData("conversations")]
    [InlineData("webhooks")]
    [InlineData("reports/volume")]
    public async Task AWorkspaceRoute_RejectsAnAnonymousCaller_With401(string path)
    {
        // The fallback AuthorizeFilter is global and path-independent, so a few
        // representative routes settle it — including an admin-only one, to show
        // 401 is decided before the role is looked at.
        var workspace = await CreateWorkspaceAsync();
        Client.DefaultRequestHeaders.Remove("X-Api-Key");

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/{path}");

        // 401, not 403: the caller has not said who they are yet.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- The 403-vs-404 policy ---------------------------------------------

    [Fact]
    public async Task AWorkspaceThatDoesNotExist_Returns403_JustLikeOneThatDoes()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();

        var foreign = await Client.GetAsync($"/api/workspaces/{other.Id}/inboxes");
        var fictional = await Client.GetAsync($"/api/workspaces/{Guid.NewGuid()}/inboxes");

        // The scope filter never touches the database, so 403 cannot be used to
        // probe which workspace ids are real.
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, fictional.StatusCode);

        var foreignProblem = await foreign.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        var fictionalProblem = await fictional.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Workspace access denied", foreignProblem!.Title);
        Assert.Equal(foreignProblem.Title, fictionalProblem!.Title);
        Assert.Equal(foreignProblem.Detail, fictionalProblem.Detail);
    }

    [Fact]
    public async Task AForeignResource_IsIndistinguishableFromOneThatNeverExisted()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await CreateWorkspaceAsync("Other");

        var foreign = await Client.GetAsync($"/api/conversations/{convo.Id}");
        var fictional = await Client.GetAsync($"/api/conversations/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, fictional.StatusCode);
    }

    /// <summary>Resource routes that must 404 rather than 403 across workspaces.</summary>
    public static TheoryData<string> ForeignConversationRoutes() =>
    [
        "/api/conversations/{0}",
        "/api/conversations/{0}/sla-breaches",
        "/api/conversations/{0}/assignment-events",
        "/api/conversations/{0}/suggested-articles",
    ];

    [Theory]
    [MemberData(nameof(ForeignConversationRoutes))]
    public async Task AForeignConversationRoute_Returns404(string template)
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await CreateWorkspaceAsync("Other");

        var response = await Client.GetAsync(string.Format(template, convo.Id));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignDirectoryResources_Return404()
    {
        var workspace = await CreateWorkspaceAsync();
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var team = await CreateTeamAsync(workspace.Id, "Theirs");
        var tag = await CreateTagAsync(workspace.Id, "theirs");
        await CreateWorkspaceAsync("Other");

        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.GetAsync($"/api/contacts/{contact.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.GetAsync($"/api/teammates/{teammate.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.GetAsync($"/api/teams/{team.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.DeleteAsync($"/api/tags/{tag.Id}")).StatusCode);
    }

    [Fact]
    public async Task WritingToAForeignResource_Also404s_RatherThan403()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await CreateWorkspaceAsync("Other");

        var state = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);
        var assignment = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/assignment",
            new AssignConversationRequest(null, null), Json);
        var priority = await Client.PutAsJsonAsync($"/api/conversations/{convo.Id}/priority",
            new SetPriorityRequest(ConversationPriority.Urgent), Json);

        Assert.Equal(HttpStatusCode.NotFound, state.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, assignment.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, priority.StatusCode);
    }

    // --- Anonymous surface --------------------------------------------------

    [Fact]
    public async Task TheAnonymousSurface_IsExactlyHealth_WorkspaceCreation_AndThePublicHelpCenter()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await ReadAsync<CollectionResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/collections",
                new CreateCollectionRequest("Guides"), Json));
        var article = await ReadAsync<ArticleResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/articles",
                new CreateArticleRequest(collection.Id, "Reset your password", "Click reset."), Json));
        await Client.PostAsync($"/api/articles/{article.Id}/publish", null);

        using var anonymous = Factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await anonymous.GetAsync($"/api/public/workspaces/{workspace.Id}/collections")).StatusCode);
        // Bootstrapping has to be anonymous — there is no key to hold yet.
        Assert.Equal(HttpStatusCode.Created,
            (await anonymous.PostAsJsonAsync("/api/workspaces",
                new CreateWorkspaceRequest("Fresh", "Ada", $"ada-{Guid.NewGuid():N}@acme.test"),
                Json)).StatusCode);
    }

    [Fact]
    public async Task ThePublicHelpCenter_IsReadableByATeammateOfAnotherWorkspace()
    {
        var workspace = await CreateWorkspaceAsync();
        var collection = await ReadAsync<CollectionResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/collections",
                new CreateCollectionRequest("Guides"), Json));
        var article = await ReadAsync<ArticleResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/articles",
                new CreateArticleRequest(collection.Id, "Public thing", "Body"), Json));
        await Client.PostAsync($"/api/articles/{article.Id}/publish", null);
        await CreateWorkspaceAsync("Other");

        // Signed in elsewhere, but these pages are public by definition — the
        // scope filter deliberately steps aside for anonymous endpoints.
        var response = await Client.GetAsync($"/api/public/workspaces/{workspace.Id}/articles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(await ReadAsync<List<PublicArticleResponse>>(response));
    }

    [Fact]
    public async Task AnUnknownApiKey_Is401_OnEveryKindOfRoute()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        ActAs("hbk_not_a_real_key_at_all");

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client.GetAsync($"/api/workspaces/{workspace.Id}/inboxes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Client.GetAsync($"/api/conversations/{convo.Id}")).StatusCode);
    }

    [Fact]
    public async Task AnEmptyApiKeyHeader_Is401_NotAServerError()
    {
        var workspace = await CreateWorkspaceAsync();
        ActAs(string.Empty);

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/inboxes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AnAgent_CannotSetAvailability_ForATeammateInAnotherWorkspace()
    {
        var other = await CreateWorkspaceAsync("Other");
        var outsider = await CreateTeammateAsync(other.Id, "Outsider");
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Agent");
        ActAs(agent.ApiKey);

        var response = await Client.PutAsJsonAsync($"/api/teammates/{outsider.Id}/availability",
            new UpdateAvailabilityRequest(TeammateAvailability.Away, null), Json);

        // Not our teammate: 403 for not being an admin, or 404 for being
        // invisible — either way, never a successful write.
        Assert.Contains(
            response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });
    }
}
