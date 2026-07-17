using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>Round-robin auto-assignment, availability, capacity, and the audit trail.</summary>
public class AssignmentRulesTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    /// <summary>
    /// Workspace with an auto-assign inbox where the bootstrap admin is Away,
    /// so only explicitly created agents participate in the rotation.
    /// </summary>
    private async Task<(WorkspaceResponse Workspace, InboxResponse Inbox, ContactResponse Contact)>
        SetUpAutoAssignWorkspaceAsync()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, autoAssign: true);
        var contact = await CreateContactAsync(workspace.Id);
        var admin = await ReadAsync<List<TeammateResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/teammates"));
        await SetAvailabilityAsync(admin.Single().Id, TeammateAvailability.Away);
        return (workspace, inbox, contact);
    }

    [Fact]
    public async Task AutoAssign_RoundRobins_AcrossAvailableTeammates()
    {
        var (workspace, inbox, contact) = await SetUpAutoAssignWorkspaceAsync();
        var first = await CreateTeammateAsync(workspace.Id, "First");
        var second = await CreateTeammateAsync(workspace.Id, "Second");

        var convo1 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var convo2 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");
        var convo3 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "three");

        var agentIds = new[] { first.Id, second.Id };
        Assert.Contains(convo1.AssignedTeammateId!.Value, agentIds);
        Assert.Contains(convo2.AssignedTeammateId!.Value, agentIds);
        Assert.NotEqual(convo1.AssignedTeammateId, convo2.AssignedTeammateId);
        // With two eligible agents the third conversation wraps around.
        Assert.Equal(convo1.AssignedTeammateId, convo3.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_SkipsAwayTeammates()
    {
        var (workspace, inbox, contact) = await SetUpAutoAssignWorkspaceAsync();
        var active = await CreateTeammateAsync(workspace.Id, "Active");
        var away = await CreateTeammateAsync(workspace.Id, "Away");
        await SetAvailabilityAsync(away.Id, TeammateAvailability.Away);

        var convo1 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var convo2 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");

        Assert.Equal(active.Id, convo1.AssignedTeammateId);
        Assert.Equal(active.Id, convo2.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_RespectsCapacityLimit()
    {
        var (workspace, inbox, contact) = await SetUpAutoAssignWorkspaceAsync();
        var limited = await CreateTeammateAsync(workspace.Id, "Limited");
        await SetAvailabilityAsync(limited.Id, TeammateAvailability.Available, capacityLimit: 1);

        var convo1 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var convo2 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");

        Assert.Equal(limited.Id, convo1.AssignedTeammateId);
        Assert.Null(convo2.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_ClosedConversations_DoNotCountTowardCapacity()
    {
        var (workspace, inbox, contact) = await SetUpAutoAssignWorkspaceAsync();
        var limited = await CreateTeammateAsync(workspace.Id, "Limited");
        await SetAvailabilityAsync(limited.Id, TeammateAvailability.Available, capacityLimit: 1);

        var convo1 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        await Client.PostAsJsonAsync(
            $"/api/conversations/{convo1.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        var convo2 = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");

        Assert.Equal(limited.Id, convo2.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_NoEligibleTeammate_LeavesUnassigned()
    {
        var (workspace, inbox, contact) = await SetUpAutoAssignWorkspaceAsync();
        // Only the Away bootstrap admin exists.

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Null(convo.AssignedTeammateId);
        var events = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/assignment-events"));
        Assert.Empty(events);
    }

    [Fact]
    public async Task AutoAssign_Disabled_LeavesUnassigned()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Null(convo.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_RecordsAutoEvent_WithoutActor()
    {
        var (workspace, inbox, contact) = await SetUpAutoAssignWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var events = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/assignment-events"));
        var auto = Assert.Single(events);
        Assert.Equal(AssignmentKind.Auto, auto.Kind);
        Assert.Null(auto.ActorTeammateId);
        Assert.Null(auto.FromTeammateId);
        Assert.Equal(agent.Id, auto.ToTeammateId);
    }

    [Fact]
    public async Task ManualReassignment_RecordsFullAuditTrail()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var team = await CreateTeamAsync(workspace.Id, "Escalations");
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var adminId = (await ReadAsync<List<TeammateResponse>>(
                await Client.GetAsync($"/api/workspaces/{workspace.Id}/teammates")))
            .Single(t => t.Role == TeammateRole.Admin).Id;

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/assignment",
            new AssignConversationRequest(teammate.Id, null), Json);
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/assignment",
            new AssignConversationRequest(null, team.Id), Json);
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/assignment",
            new AssignConversationRequest(null, null), Json);

        var events = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/assignment-events"));

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(AssignmentKind.Manual, e.Kind));
        Assert.All(events, e => Assert.Equal(adminId, e.ActorTeammateId));

        Assert.Null(events[0].FromTeammateId);
        Assert.Equal(teammate.Id, events[0].ToTeammateId);

        Assert.Equal(teammate.Id, events[1].FromTeammateId);
        Assert.Equal(team.Id, events[1].ToTeamId);
        Assert.Null(events[1].ToTeammateId);

        Assert.Equal(team.Id, events[2].FromTeamId);
        Assert.Null(events[2].ToTeammateId);
        Assert.Null(events[2].ToTeamId);
    }

    [Fact]
    public async Task AssignmentEvents_ForForeignConversation_Returns404()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await CreateWorkspaceAsync("Other");

        var response = await Client.GetAsync($"/api/conversations/{convo.Id}/assignment-events");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Availability_SelfService_RoundTrips()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Self");
        ActAs(agent.ApiKey);

        var updated = await SetAvailabilityAsync(agent.Id, TeammateAvailability.Away, capacityLimit: 5);

        Assert.Equal(TeammateAvailability.Away, updated.Availability);
        Assert.Equal(5, updated.CapacityLimit);
    }

    [Fact]
    public async Task Availability_AgentCannotChangeOthers_AdminCan()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Agent");
        var other = await CreateTeammateAsync(workspace.Id, "Other");

        ActAs(agent.ApiKey);
        var denied = await Client.PutAsJsonAsync(
            $"/api/teammates/{other.Id}/availability",
            new UpdateAvailabilityRequest(TeammateAvailability.Away, null), Json);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        ActAsAdminOf(workspace.Id);
        var allowed = await SetAvailabilityAsync(other.Id, TeammateAvailability.Away);
        Assert.Equal(TeammateAvailability.Away, allowed.Availability);
    }
}
