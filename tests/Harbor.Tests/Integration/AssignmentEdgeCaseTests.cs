using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>
/// The edges of round-robin assignment: fairness over a long run, the
/// availability and capacity limits that make a teammate ineligible, and the
/// teammate-XOR-team invariant.
/// </summary>
public class AssignmentEdgeCaseTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    /// <summary>
    /// An auto-assign inbox whose only participants are the agents the test
    /// creates: the bootstrap admin is parked Away so it never joins the
    /// rotation and skews the counts.
    /// </summary>
    private async Task<(WorkspaceResponse Workspace, InboxResponse Inbox, ContactResponse Contact)>
        SetUpAsync()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, autoAssign: true);
        var contact = await CreateContactAsync(workspace.Id);
        var admin = await ReadAsync<List<TeammateResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/teammates"));
        await SetAvailabilityAsync(admin.Single().Id, TeammateAvailability.Away);
        return (workspace, inbox, contact);
    }

    private async Task<List<Guid?>> StartManyAsync(
        Guid workspaceId, Guid inboxId, Guid contactId, int count)
    {
        var assignees = new List<Guid?>();
        foreach (var i in Enumerable.Range(0, count))
        {
            var convo = await StartConversationAsync(workspaceId, inboxId, contactId, $"convo {i}");
            assignees.Add(convo.AssignedTeammateId);
        }

        return assignees;
    }

    // --- Fairness ---------------------------------------------------------

    [Fact]
    public async Task AutoAssign_SpreadsWorkEvenly_OverAFullRotation()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var agents = new List<Guid>();
        foreach (var name in new[] { "First", "Second", "Third" })
        {
            agents.Add((await CreateTeammateAsync(workspace.Id, name)).Id);
        }

        var assignees = await StartManyAsync(workspace.Id, inbox.Id, contact.Id, 9);

        // Three rotations over three agents: everyone carries exactly three.
        Assert.All(assignees, a => Assert.Contains(a!.Value, agents));
        foreach (var agent in agents)
        {
            Assert.Equal(3, assignees.Count(a => a == agent));
        }
    }

    [Fact]
    public async Task AutoAssign_VisitsEveryoneOnce_BeforeRepeating()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        await CreateTeammateAsync(workspace.Id, "First");
        await CreateTeammateAsync(workspace.Id, "Second");
        await CreateTeammateAsync(workspace.Id, "Third");

        var assignees = await StartManyAsync(workspace.Id, inbox.Id, contact.Id, 3);

        // No one is asked twice while someone is still waiting for their turn.
        Assert.Equal(3, assignees.Distinct().Count());
    }

    // --- Availability edges -----------------------------------------------

    [Fact]
    public async Task AutoAssign_WhenEveryoneIsAway_LeavesUnassigned()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var first = await CreateTeammateAsync(workspace.Id, "First");
        var second = await CreateTeammateAsync(workspace.Id, "Second");
        await SetAvailabilityAsync(first.Id, TeammateAvailability.Away);
        await SetAvailabilityAsync(second.Id, TeammateAvailability.Away);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        // Unassigned rather than assigned-to-nobody-in-particular: the
        // conversation still exists and still has its SLA running.
        Assert.Null(convo.AssignedTeammateId);
        Assert.Null(convo.AssignedTeamId);
        Assert.Equal(ConversationState.Open, convo.State);
    }

    [Fact]
    public async Task AutoAssign_SkipsATeammateWhoGoesAway_MidRotation()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var staying = await CreateTeammateAsync(workspace.Id, "Staying");
        var leaving = await CreateTeammateAsync(workspace.Id, "Leaving");

        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        await SetAvailabilityAsync(leaving.Id, TeammateAvailability.Away);
        var second = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");
        var third = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "three");

        Assert.Equal(staying.Id, first.AssignedTeammateId);
        // The rotation carries on from an assignee who is no longer eligible.
        Assert.Equal(staying.Id, second.AssignedTeammateId);
        Assert.Equal(staying.Id, third.AssignedTeammateId);
        Assert.NotEqual(leaving.Id, second.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_ResumesForATeammateWhoComesBack()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var away = await CreateTeammateAsync(workspace.Id, "Away");
        await SetAvailabilityAsync(away.Id, TeammateAvailability.Away);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "while away");

        await SetAvailabilityAsync(away.Id, TeammateAvailability.Available);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "after return");

        Assert.Equal(away.Id, convo.AssignedTeammateId);
    }

    // --- Capacity edges ---------------------------------------------------

    [Fact]
    public async Task AutoAssign_WhenEveryoneIsAtCapacity_LeavesUnassigned()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var first = await CreateTeammateAsync(workspace.Id, "First");
        var second = await CreateTeammateAsync(workspace.Id, "Second");
        await SetAvailabilityAsync(first.Id, TeammateAvailability.Available, capacityLimit: 1);
        await SetAvailabilityAsync(second.Id, TeammateAvailability.Available, capacityLimit: 1);

        var assignees = await StartManyAsync(workspace.Id, inbox.Id, contact.Id, 3);

        Assert.NotNull(assignees[0]);
        Assert.NotNull(assignees[1]);
        Assert.NotEqual(assignees[0], assignees[1]);
        // Both are now full; the queue holds the overflow rather than
        // overloading someone.
        Assert.Null(assignees[2]);
    }

    [Fact]
    public async Task AutoAssign_CapacityBoundary_AdmitsUpToTheLimit_AndNotPast()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");
        await SetAvailabilityAsync(agent.Id, TeammateAvailability.Available, capacityLimit: 3);

        var assignees = await StartManyAsync(workspace.Id, inbox.Id, contact.Id, 4);

        // Eligibility is open < cap, so the third conversation fills the limit
        // and the fourth finds nobody.
        Assert.Equal([agent.Id, agent.Id, agent.Id], assignees.Take(3));
        Assert.Null(assignees[3]);
    }

    [Fact]
    public async Task AutoAssign_SnoozedConversations_StillCountTowardCapacity()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");
        await SetAvailabilityAsync(agent.Id, TeammateAvailability.Available, capacityLimit: 1);
        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");

        await Client.PostAsJsonAsync($"/api/conversations/{first.Id}/state",
            new ChangeStateRequest(ConversationState.Snoozed, DateTimeOffset.UtcNow.AddHours(3)), Json);
        var second = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");

        // Snoozing is not finishing: the work is still owed, so it still
        // occupies a slot. Only closing frees capacity.
        Assert.Null(second.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_ManuallyAssignedWork_AlsoConsumesCapacity()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");
        await SetAvailabilityAsync(agent.Id, TeammateAvailability.Available, capacityLimit: 1);
        var chatInbox = await CreateInboxAsync(workspace.Id, "Manual");
        var manual = await StartConversationAsync(workspace.Id, chatInbox.Id, contact.Id, "manual");
        await AssignAsync(manual.Id, agent.Id);

        var auto = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "auto");

        // Capacity is a property of the person, not of one inbox.
        Assert.Null(auto.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_ClosingWork_FreesCapacityForTheNextConversation()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");
        await SetAvailabilityAsync(agent.Id, TeammateAvailability.Available, capacityLimit: 1);
        var first = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "one");
        var blocked = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "two");

        await Client.PostAsJsonAsync($"/api/conversations/{first.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);
        var third = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "three");

        Assert.Null(blocked.AssignedTeammateId);
        Assert.Equal(agent.Id, third.AssignedTeammateId);
    }

    [Fact]
    public async Task AutoAssign_NoCapacityLimit_MeansUnlimited()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Tireless");

        var assignees = await StartManyAsync(workspace.Id, inbox.Id, contact.Id, 5);

        Assert.All(assignees, a => Assert.Equal(agent.Id, a));
    }

    // --- The teammate-XOR-team invariant ----------------------------------

    [Fact]
    public async Task Assignment_NeverHoldsBothATeammateAndATeam()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var team = await CreateTeamAsync(workspace.Id, "Escalations");
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        // Walk the assignment target back and forth; the invariant has to hold
        // after each move, not just the last one.
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var toTeammate = await AssignAsync(convo.Id, teammateId: teammate.Id);
            Assert.Equal(teammate.Id, toTeammate.AssignedTeammateId);
            Assert.Null(toTeammate.AssignedTeamId);

            var toTeam = await AssignAsync(convo.Id, teamId: team.Id);
            Assert.Equal(team.Id, toTeam.AssignedTeamId);
            Assert.Null(toTeam.AssignedTeammateId);
        }

        Factory.WithDb(db =>
        {
            var stored = db.Conversations.Single(c => c.Id == convo.Id);
            Assert.False(stored.AssignedTeammateId is not null && stored.AssignedTeamId is not null);
        });
    }

    [Fact]
    public async Task AutoAssignment_ToATeammate_ClearsAnyTeamAssignment()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Equal(agent.Id, convo.AssignedTeammateId);
        Assert.Null(convo.AssignedTeamId);
    }

    // --- Audit trail ------------------------------------------------------

    [Fact]
    public async Task ManualReassignment_AfterAutoAssign_RecordsTheAutoAssigneeAsFrom()
    {
        var (workspace, inbox, contact) = await SetUpAsync();
        var auto = await CreateTeammateAsync(workspace.Id, "Auto");
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var takeover = await CreateTeammateAsync(workspace.Id, "Takeover");

        await AssignAsync(convo.Id, takeover.Id);

        var events = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/assignment-events"));

        // The trail has to join up: the manual move's From must be whoever the
        // round-robin picked, or the history lies about who dropped the work.
        Assert.Equal(2, events.Count);
        Assert.Equal(AssignmentKind.Auto, events[0].Kind);
        Assert.Equal(auto.Id, events[0].ToTeammateId);
        Assert.Equal(AssignmentKind.Manual, events[1].Kind);
        Assert.Equal(auto.Id, events[1].FromTeammateId);
        Assert.Equal(takeover.Id, events[1].ToTeammateId);
    }

    [Fact]
    public async Task AssignmentTrail_SurvivesTheTeammateBeingDeleted()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id, "Leaver");
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await AssignAsync(convo.Id, teammate.Id);

        // Actor/from/to ids are deliberately unconstrained so directory
        // changes cannot rewrite history.
        Factory.WithDb(db =>
        {
            db.Teammates.Remove(db.Teammates.Single(t => t.Id == teammate.Id));
            db.SaveChanges();
        });

        var events = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/assignment-events"));
        Assert.Equal(teammate.Id, Assert.Single(events).ToTeammateId);
    }
}
