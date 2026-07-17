using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>
/// The role half of the authorization matrix: every admin-only endpoint swept
/// against an Agent and against an Admin.
///
/// <see cref="AuthorizationTests"/> covers the mechanism on a few
/// representative routes; this covers the table, so an endpoint added without
/// its role attribute is caught here rather than by whoever happens to look.
/// Workspace scoping and the 403-vs-404 policy live in
/// <see cref="WorkspaceScopeMatrixTests"/> — kept apart so the two sweeps run
/// as separate xUnit collections instead of one long serial queue.
/// </summary>
public class AuthorizationMatrixTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private static StringContent Body() => new("{}", Encoding.UTF8, "application/json");

    /// <summary>
    /// Workspace-scoped endpoints only an Admin may call. Authorization runs
    /// before model binding, so an empty body still exercises the role check.
    /// </summary>
    public static TheoryData<string, string> AdminOnlyEndpoints() => new()
    {
        { "POST", "inboxes" },
        { "POST", "teammates" },
        { "POST", "teams" },
        { "POST", "tags" },
        { "POST", "canned-replies" },
        { "POST", "sla-policies" },
        { "POST", "segments" },
        { "POST", "webhooks" },
        { "GET", "webhooks" },
        { "POST", "webhooks/dispatch" },
        { "POST", "collections" },
        { "POST", "articles" },
    };

    /// <summary>Workspace-scoped endpoints any authenticated teammate may read.</summary>
    public static TheoryData<string> AgentReadableEndpoints() =>
    [
        "inboxes",
        "conversations",
        "contacts",
        "teammates",
        "teams",
        "tags",
        "canned-replies",
        "sla-policies",
        "segments",
        "collections",
        "articles",
        "reports/volume",
        "reports/response-times",
        "reports/teammates",
        "reports/inboxes",
        "reports/tags",
    ];

    internal async Task<HttpResponseMessage> SendAsync(string method, string url) =>
        method == "GET" ? await Client.GetAsync(url) : await Client.PostAsync(url, Body());

    [Theory]
    [MemberData(nameof(AdminOnlyEndpoints))]
    public async Task AdminOnlyEndpoint_RejectsAnAgent_With403(string method, string path)
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id);
        ActAs(agent.ApiKey);

        var response = await SendAsync(method, $"/api/workspaces/{workspace.Id}/{path}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminOnlyEndpoints))]
    public async Task AdminOnlyEndpoint_AdmitsAnAdmin(string method, string path)
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await SendAsync(method, $"/api/workspaces/{workspace.Id}/{path}");

        // The empty body may well fail validation — what matters is that the
        // request got past authorization to be judged on its merits.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AgentReadableEndpoints))]
    public async Task ReadableEndpoint_AdmitsAnAgent(string path)
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id);
        ActAs(agent.ApiKey);

        var response = await Client.GetAsync($"/api/workspaces/{workspace.Id}/{path}");

        // Agents do the actual support work; reading is the job.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- Self-service vs administration -------------------------------------

    [Fact]
    public async Task Availability_IsSelfService_ForAgents_ButAdminsMayActOnAnyone()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Agent");
        var colleague = await CreateTeammateAsync(workspace.Id, "Colleague");

        ActAs(agent.ApiKey);
        var onSelf = await Client.PutAsJsonAsync($"/api/teammates/{agent.Id}/availability",
            new Api.Contracts.UpdateAvailabilityRequest(TeammateAvailability.Away, null), Json);
        var onColleague = await Client.PutAsJsonAsync($"/api/teammates/{colleague.Id}/availability",
            new Api.Contracts.UpdateAvailabilityRequest(TeammateAvailability.Away, null), Json);

        Assert.Equal(HttpStatusCode.OK, onSelf.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, onColleague.StatusCode);

        ActAsAdminOf(workspace.Id);
        var asAdmin = await Client.PutAsJsonAsync($"/api/teammates/{colleague.Id}/availability",
            new Api.Contracts.UpdateAvailabilityRequest(TeammateAvailability.Away, null), Json);
        Assert.Equal(HttpStatusCode.OK, asAdmin.StatusCode);
    }

    [Fact]
    public async Task AnAgent_CanRunTheSupportWorkflow_EndToEnd()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var agent = await CreateTeammateAsync(workspace.Id, "Agent");
        ActAs(agent.ApiKey);

        // The whole point of the Agent role: everything a support rep does in a
        // day must work without an admin key.
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var reply = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new Api.Contracts.AddMessageRequest(
                AuthorType.Teammate, agent.Id, MessageKind.Reply, "On it."), Json);
        var assigned = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/assignment",
            new Api.Contracts.AssignConversationRequest(agent.Id, null), Json);
        var closed = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new Api.Contracts.ChangeStateRequest(ConversationState.Closed, null), Json);

        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
    }
}
