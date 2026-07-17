using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>Exercises the demo seed data end-to-end through the API.</summary>
public class SeedDataSmokeTests : ApiTestBase
{
    public SeedDataSmokeTests(HarborApiFactory factory) : base(factory)
    {
        factory.SeedDemoData();
    }

    private async Task<WorkspaceResponse> GetSeededWorkspaceAsync()
    {
        var workspaces = await ReadAsync<List<WorkspaceResponse>>(await Client.GetAsync("/api/workspaces"));
        return workspaces.Single(w => w.Name == "Acme Support");
    }

    [Fact]
    public async Task SeededWorkspace_IsFullyNavigable()
    {
        var workspace = await GetSeededWorkspaceAsync();

        var inboxes = await ReadAsync<List<InboxResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/inboxes"));
        var teammates = await ReadAsync<List<TeammateResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/teammates"));
        var conversations = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations"));

        Assert.Equal(2, inboxes.Count);
        Assert.Equal(3, teammates.Count);
        Assert.Equal(4, conversations.Count);
    }

    [Fact]
    public async Task SeededConversations_CoverAllStates()
    {
        var workspace = await GetSeededWorkspaceAsync();

        var conversations = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations"));

        Assert.Contains(conversations, c => c.State == ConversationState.Open);
        Assert.Contains(conversations, c => c.State == ConversationState.Snoozed);
        Assert.Contains(conversations, c => c.State == ConversationState.Closed);
    }

    [Fact]
    public async Task SeededSlaBreach_IsFoundByFilter()
    {
        var workspace = await GetSeededWorkspaceAsync();

        var breached = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?slaBreached=true"));

        var convo = Assert.Single(breached);
        Assert.Equal("Cannot log in to my account", convo.Subject);
        Assert.Null(convo.FirstRespondedAt);
    }

    [Fact]
    public async Task SeededThread_ContainsNote()
    {
        var workspace = await GetSeededWorkspaceAsync();
        var conversations = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?q=charged twice"));
        var billing = Assert.Single(conversations);

        var detail = await ReadAsync<ConversationDetailResponse>(
            await Client.GetAsync($"/api/conversations/{billing.Id}"));

        Assert.Equal(3, detail.Messages.Count);
        Assert.Contains(detail.Messages, m => m.Kind == MessageKind.Note);
        Assert.Equal(["billing", "vip"], detail.Tags);
    }
}
