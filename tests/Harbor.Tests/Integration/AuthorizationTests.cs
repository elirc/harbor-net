using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>API-key authentication, role checks, and workspace isolation.</summary>
public class AuthorizationTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    [Fact]
    public async Task Request_WithoutApiKey_Returns401()
    {
        var workspace = await CreateWorkspaceAsync();
        Client.DefaultRequestHeaders.Remove("X-Api-Key");

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/inboxes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Request_WithUnknownApiKey_Returns401()
    {
        var workspace = await CreateWorkspaceAsync();
        ActAs("hbk_definitely_not_a_real_key");

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/inboxes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_IsAnonymous()
    {
        Client.DefaultRequestHeaders.Remove("X-Api-Key");

        var response = await Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task WorkspaceRoute_WithForeignKey_Returns403()
    {
        var workspace = await CreateWorkspaceAsync();
        var other = await CreateWorkspaceAsync("Other");
        // Client is now authenticated as Other's admin.

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/inboxes");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(workspace.Id, other.Id);
    }

    [Fact]
    public async Task ResourceRoute_AcrossWorkspaces_Returns404()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await CreateWorkspaceAsync("Other");
        // Acting as Other's admin: the foreign conversation must be invisible.

        var conversation = await Client.GetAsync($"/api/conversations/{convo.Id}");
        var contactResponse = await Client.GetAsync($"/api/contacts/{contact.Id}");

        Assert.Equal(HttpStatusCode.NotFound, conversation.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, contactResponse.StatusCode);
    }

    [Fact]
    public async Task Agent_CannotCreateTeammates()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Agent Smith");
        ActAs(agent.ApiKey);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/teammates",
            new CreateTeammateRequest("Sneaky", "sneaky@acme.test"), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Agent_CannotManageDirectory_ButCanConverse()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var agent = await CreateTeammateAsync(workspace.Id, "Agent Smith");
        ActAs(agent.ApiKey);

        var createInbox = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/inboxes", new CreateInboxRequest("Nope", null), Json);
        var createTag = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/tags", new CreateTagRequest("nope"), Json);
        Assert.Equal(HttpStatusCode.Forbidden, createInbox.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, createTag.StatusCode);

        var start = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/conversations",
            new StartConversationRequest(inbox.Id, contact.Id, "From agent", "hello"), Json);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);

        var convo = await ReadAsync<ConversationDetailResponse>(start);
        var reply = await Client.PostAsJsonAsync(
            $"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, agent.Id, MessageKind.Reply, "On it."), Json);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
    }

    [Fact]
    public async Task AdminTeammate_CreatedWithAdminRole_CanManageDirectory()
    {
        var workspace = await CreateWorkspaceAsync();
        var admin = await CreateTeammateAsync(
            workspace.Id, "Second Admin", role: TeammateRole.Admin);
        ActAs(admin.ApiKey);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/inboxes", new CreateInboxRequest("Allowed", null), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreatedTeammateApiKey_Authenticates_AsThatTeammate()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Keyed Agent");
        Assert.StartsWith("hbk_", agent.ApiKey);
        ActAs(agent.ApiKey);

        var me = await ReadAsync<TeammateResponse>(
            await Client.GetAsync($"/api/teammates/{agent.Id}"));

        Assert.Equal(agent.Id, me.Id);
        Assert.Equal(TeammateRole.Agent, me.Role);
    }

    [Fact]
    public async Task TeammateList_DoesNotLeakApiKeys()
    {
        var workspace = await CreateWorkspaceAsync();
        await CreateTeammateAsync(workspace.Id);

        var raw = await Client.GetStringAsync($"/api/workspaces/{workspace.Id}/teammates");

        Assert.DoesNotContain("apiKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hbk_", raw);
    }
}
