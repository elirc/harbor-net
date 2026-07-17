using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Api.Contracts;

namespace Harbor.Tests.Integration;

/// <summary>Shared HTTP helpers for integration tests.</summary>
public abstract class ApiTestBase(HarborApiFactory factory) : IClassFixture<HarborApiFactory>
{
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    protected HarborApiFactory Factory { get; } = factory;
    protected HttpClient Client { get; } = factory.CreateClient();

    protected async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    protected async Task<WorkspaceResponse> CreateWorkspaceAsync(string name = "Test Workspace")
    {
        var response = await Client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(name), Json);
        return await ReadAsync<WorkspaceResponse>(response);
    }

    protected async Task<InboxResponse> CreateInboxAsync(
        Guid workspaceId, string name = "Support", int? slaMinutes = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/inboxes", new CreateInboxRequest(name, slaMinutes), Json);
        return await ReadAsync<InboxResponse>(response);
    }

    protected async Task<ContactResponse> CreateContactAsync(
        Guid workspaceId, string name = "Test Contact", string? email = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/contacts", new CreateContactRequest(name, email, null), Json);
        return await ReadAsync<ContactResponse>(response);
    }

    protected async Task<TeammateResponse> CreateTeammateAsync(
        Guid workspaceId, string name = "Test Agent", string? email = null)
    {
        email ??= $"{Guid.NewGuid():N}@acme.test";
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/teammates", new CreateTeammateRequest(name, email), Json);
        return await ReadAsync<TeammateResponse>(response);
    }

    protected async Task<TeamResponse> CreateTeamAsync(Guid workspaceId, string name)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/teams", new CreateTeamRequest(name), Json);
        return await ReadAsync<TeamResponse>(response);
    }

    protected async Task<ConversationDetailResponse> StartConversationAsync(
        Guid workspaceId, Guid inboxId, Guid contactId,
        string? subject = "Need help", string body = "Hello, something is broken.")
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/conversations",
            new StartConversationRequest(inboxId, contactId, subject, body), Json);
        return await ReadAsync<ConversationDetailResponse>(response);
    }
}
