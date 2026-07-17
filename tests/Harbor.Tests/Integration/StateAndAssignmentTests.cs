using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

public class StateAndAssignmentTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<(Guid WorkspaceId, ConversationDetailResponse Convo)> SetUpConversationAsync()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        return (workspace.Id, convo);
    }

    private async Task<HttpResponseMessage> ChangeStateAsync(
        Guid conversationId, ConversationState state, DateTimeOffset? snoozedUntil = null)
    {
        return await Client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/state",
            new ChangeStateRequest(state, snoozedUntil), Json);
    }

    private async Task<HttpResponseMessage> AssignAsync(
        Guid conversationId, Guid? teammateId = null, Guid? teamId = null)
    {
        return await Client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/assignment",
            new AssignConversationRequest(teammateId, teamId), Json);
    }

    [Fact]
    public async Task Close_SetsStateAndClosedAt()
    {
        var (_, convo) = await SetUpConversationAsync();

        var result = await ReadAsync<ConversationSummaryResponse>(
            await ChangeStateAsync(convo.Id, ConversationState.Closed));

        Assert.Equal(ConversationState.Closed, result.State);
        Assert.NotNull(result.ClosedAt);
    }

    [Fact]
    public async Task Snooze_WithFutureTime_SetsSnoozedUntil()
    {
        var (_, convo) = await SetUpConversationAsync();
        var until = DateTimeOffset.UtcNow.AddHours(4);

        var result = await ReadAsync<ConversationSummaryResponse>(
            await ChangeStateAsync(convo.Id, ConversationState.Snoozed, until));

        Assert.Equal(ConversationState.Snoozed, result.State);
        Assert.Equal(until, result.SnoozedUntil);
    }

    [Fact]
    public async Task Snooze_WithoutTime_Returns422()
    {
        var (_, convo) = await SetUpConversationAsync();

        var response = await ChangeStateAsync(convo.Id, ConversationState.Snoozed);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Snooze_InThePast_Returns422()
    {
        var (_, convo) = await SetUpConversationAsync();

        var response = await ChangeStateAsync(
            convo.Id, ConversationState.Snoozed, DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Reopen_ClearsSnoozeAndClose_Metadata()
    {
        var (_, convo) = await SetUpConversationAsync();
        await ChangeStateAsync(convo.Id, ConversationState.Closed);

        var result = await ReadAsync<ConversationSummaryResponse>(
            await ChangeStateAsync(convo.Id, ConversationState.Open));

        Assert.Equal(ConversationState.Open, result.State);
        Assert.Null(result.ClosedAt);
        Assert.Null(result.SnoozedUntil);
    }

    [Fact]
    public async Task ChangeState_UnknownConversation_Returns404()
    {
        await CreateWorkspaceAsync();

        var response = await ChangeStateAsync(Guid.NewGuid(), ConversationState.Closed);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssignTeammate_ThenTeam_SwapsAssignment()
    {
        var (workspaceId, convo) = await SetUpConversationAsync();
        var teammate = await CreateTeammateAsync(workspaceId);
        var team = await CreateTeamAsync(workspaceId, "Escalations");

        var afterTeammate = await ReadAsync<ConversationSummaryResponse>(
            await AssignAsync(convo.Id, teammateId: teammate.Id));
        Assert.Equal(teammate.Id, afterTeammate.AssignedTeammateId);
        Assert.Null(afterTeammate.AssignedTeamId);

        var afterTeam = await ReadAsync<ConversationSummaryResponse>(
            await AssignAsync(convo.Id, teamId: team.Id));
        Assert.Equal(team.Id, afterTeam.AssignedTeamId);
        Assert.Null(afterTeam.AssignedTeammateId);
    }

    [Fact]
    public async Task Assign_WithBothTargets_Returns422()
    {
        var (workspaceId, convo) = await SetUpConversationAsync();
        var teammate = await CreateTeammateAsync(workspaceId);
        var team = await CreateTeamAsync(workspaceId, "Both");

        var response = await AssignAsync(convo.Id, teammate.Id, team.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Assign_WithNoTargets_Unassigns()
    {
        var (workspaceId, convo) = await SetUpConversationAsync();
        var teammate = await CreateTeammateAsync(workspaceId);
        await AssignAsync(convo.Id, teammateId: teammate.Id);

        var result = await ReadAsync<ConversationSummaryResponse>(await AssignAsync(convo.Id));

        Assert.Null(result.AssignedTeammateId);
        Assert.Null(result.AssignedTeamId);
    }

    [Fact]
    public async Task Assign_TeammateFromOtherWorkspace_Returns422()
    {
        var (workspaceId, convo) = await SetUpConversationAsync();
        var otherWorkspace = await CreateWorkspaceAsync("Other");
        var outsider = await CreateTeammateAsync(otherWorkspace.Id);
        ActAsAdminOf(workspaceId);

        var response = await AssignAsync(convo.Id, teammateId: outsider.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Assign_UnknownTeam_Returns422()
    {
        var (_, convo) = await SetUpConversationAsync();

        var response = await AssignAsync(convo.Id, teamId: Guid.NewGuid());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }
}
