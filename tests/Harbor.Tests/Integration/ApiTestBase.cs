using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Api.Contracts;
using Harbor.Domain;

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

    /// <summary>Bootstrap-admin API keys per workspace, captured on creation.</summary>
    protected Dictionary<Guid, string> AdminApiKeys { get; } = [];

    /// <summary>Sends all subsequent requests with the given API key.</summary>
    protected void ActAs(string apiKey)
    {
        Client.DefaultRequestHeaders.Remove("X-Api-Key");
        Client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
    }

    /// <summary>Sends all subsequent requests as the workspace's bootstrap admin.</summary>
    protected void ActAsAdminOf(Guid workspaceId) => ActAs(AdminApiKeys[workspaceId]);

    protected async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    /// <summary>
    /// Bootstraps a workspace with an admin teammate and switches the client
    /// to that admin's API key.
    /// </summary>
    protected async Task<WorkspaceResponse> CreateWorkspaceAsync(string name = "Test Workspace")
    {
        var response = await Client.PostAsJsonAsync(
            "/api/workspaces",
            new CreateWorkspaceRequest(name, "Boot Admin", $"admin-{Guid.NewGuid():N}@acme.test"),
            Json);
        var created = await ReadAsync<CreateWorkspaceResponse>(response);

        AdminApiKeys[created.Workspace.Id] = created.ApiKey;
        ActAs(created.ApiKey);
        return created.Workspace;
    }

    protected async Task<InboxResponse> CreateInboxAsync(
        Guid workspaceId, string name = "Support", int? slaMinutes = null, bool autoAssign = false)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/inboxes",
            new CreateInboxRequest(name, slaMinutes, autoAssign), Json);
        return await ReadAsync<InboxResponse>(response);
    }

    protected async Task<TeammateResponse> SetAvailabilityAsync(
        Guid teammateId, TeammateAvailability availability, int? capacityLimit = null)
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/teammates/{teammateId}/availability",
            new UpdateAvailabilityRequest(availability, capacityLimit), Json);
        return await ReadAsync<TeammateResponse>(response);
    }

    protected async Task<SlaPolicyResponse> CreateSlaPolicyAsync(
        Guid workspaceId, string name = "Standard", Guid? inboxId = null,
        ConversationPriority? priority = null, int? firstResponseMinutes = 60,
        int? resolutionMinutes = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/sla-policies",
            new CreateSlaPolicyRequest(name, inboxId, priority, firstResponseMinutes, resolutionMinutes),
            Json);
        return await ReadAsync<SlaPolicyResponse>(response);
    }

    /// <summary>
    /// Rewinds a conversation's whole clock so its SLA targets fall in the
    /// past, letting breach paths be exercised without waiting or faking a
    /// clock. Every timestamp moves together, so work already done on time
    /// stays on time — only the wall clock advances relative to the targets.
    /// </summary>
    protected void BackdateConversation(Guid conversationId, TimeSpan by) =>
        Factory.WithDb(db =>
        {
            var convo = db.Conversations.Single(c => c.Id == conversationId);
            convo.CreatedAt -= by;
            convo.UpdatedAt -= by;
            convo.LastMessageAt -= by;
            convo.FirstResponseDueAt -= by;
            convo.FirstRespondedAt -= by;
            convo.ResolutionDueAt -= by;
            convo.FirstResolvedAt -= by;
            db.SaveChanges();
        });

    /// <summary>
    /// Pins a conversation's lifecycle timestamps so reports have exact,
    /// assertable durations instead of whatever the wall clock produced.
    /// </summary>
    protected void SetConversationTimings(
        Guid conversationId, DateTimeOffset createdAt,
        DateTimeOffset? firstRespondedAt = null, DateTimeOffset? firstResolvedAt = null)
    {
        Factory.WithDb(db =>
        {
            var convo = db.Conversations.Single(c => c.Id == conversationId);
            convo.CreatedAt = createdAt;
            convo.UpdatedAt = createdAt;
            convo.LastMessageAt = firstResolvedAt ?? firstRespondedAt ?? createdAt;
            convo.FirstRespondedAt = firstRespondedAt;
            convo.FirstResolvedAt = firstResolvedAt;
            if (firstResolvedAt is not null)
            {
                convo.State = Domain.ConversationState.Closed;
                convo.ClosedAt = firstResolvedAt;
            }

            db.SaveChanges();
        });
    }

    protected async Task<ConversationSummaryResponse> AssignAsync(
        Guid conversationId, Guid? teammateId = null, Guid? teamId = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/assignment",
            new AssignConversationRequest(teammateId, teamId), Json);
        return await ReadAsync<ConversationSummaryResponse>(response);
    }

    protected async Task<ConversationSummaryResponse> SetPriorityAsync(
        Guid conversationId, ConversationPriority priority)
    {
        var response = await Client.PutAsJsonAsync(
            $"/api/conversations/{conversationId}/priority", new SetPriorityRequest(priority), Json);
        return await ReadAsync<ConversationSummaryResponse>(response);
    }

    protected async Task<TagResponse> CreateTagAsync(Guid workspaceId, string name)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/tags", new CreateTagRequest(name), Json);
        return await ReadAsync<TagResponse>(response);
    }

    protected async Task TagConversationAsync(Guid conversationId, Guid tagId) =>
        (await Client.PutAsync($"/api/conversations/{conversationId}/tags/{tagId}", null))
            .EnsureSuccessStatusCode();

    protected async Task<ContactResponse> CreateContactAsync(
        Guid workspaceId, string name = "Test Contact", string? email = null)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/contacts", new CreateContactRequest(name, email, null), Json);
        return await ReadAsync<ContactResponse>(response);
    }

    protected async Task<TeammateCreatedResponse> CreateTeammateAsync(
        Guid workspaceId, string name = "Test Agent", string? email = null,
        TeammateRole role = TeammateRole.Agent)
    {
        email ??= $"{Guid.NewGuid():N}@acme.test";
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/teammates", new CreateTeammateRequest(name, email, role), Json);
        return await ReadAsync<TeammateCreatedResponse>(response);
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
