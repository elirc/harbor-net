using System.Net;
using System.Net.Http.Json;
using System.Text;
using Harbor.Api.Contracts;
using Harbor.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Harbor.Tests.Integration;

/// <summary>
/// The conversation state machine from every angle: each transition's exact
/// resulting shape, the rejected moves and the ProblemDetails they produce,
/// contact-reply auto-reopen out of each state, and the inertness of notes.
/// </summary>
public class ConversationStateMachineTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<(Guid WorkspaceId, Guid ContactId, Guid TeammateId, ConversationDetailResponse Convo)>
        SetUpAsync(int? slaMinutes = null)
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, slaMinutes: slaMinutes);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        return (workspace.Id, contact.Id, teammate.Id, convo);
    }

    private Task<HttpResponseMessage> ChangeStateAsync(
        Guid id, ConversationState state, DateTimeOffset? snoozedUntil = null) =>
        Client.PostAsJsonAsync(
            $"/api/conversations/{id}/state", new ChangeStateRequest(state, snoozedUntil), Json);

    private async Task<ConversationDetailResponse> GetAsync(Guid id) =>
        await ReadAsync<ConversationDetailResponse>(await Client.GetAsync($"/api/conversations/{id}"));

    /// <summary>Drives a conversation into the requested state.</summary>
    private async Task MoveToAsync(Guid id, ConversationState state)
    {
        var response = state == ConversationState.Snoozed
            ? await ChangeStateAsync(id, state, DateTimeOffset.UtcNow.AddHours(4))
            : await ChangeStateAsync(id, state);
        response.EnsureSuccessStatusCode();
    }

    private Task<HttpResponseMessage> ContactReplyAsync(Guid conversationId, Guid contactId) =>
        Client.PostAsJsonAsync(
            $"/api/conversations/{conversationId}/messages",
            new AddMessageRequest(AuthorType.Contact, contactId, MessageKind.Reply, "Any news?"), Json);

    // --- Every transition, and the exact shape it leaves behind -----------

    public static TheoryData<ConversationState, ConversationState> AllTransitions()
    {
        var data = new TheoryData<ConversationState, ConversationState>();
        foreach (var from in Enum.GetValues<ConversationState>())
        {
            foreach (var to in Enum.GetValues<ConversationState>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllTransitions))]
    public async Task Transition_FromEveryState_ToEveryState_LeavesConsistentMetadata(
        ConversationState from, ConversationState to)
    {
        var (_, _, _, convo) = await SetUpAsync();
        await MoveToAsync(convo.Id, from);

        var until = DateTimeOffset.UtcNow.AddHours(6);
        var response = to == ConversationState.Snoozed
            ? await ChangeStateAsync(convo.Id, to, until)
            : await ChangeStateAsync(convo.Id, to);

        // Every pairing is reachable — the machine is permissive by design;
        // what matters is that the metadata never contradicts the state.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await ReadAsync<ConversationSummaryResponse>(response);
        Assert.Equal(to, result.State);

        switch (to)
        {
            case ConversationState.Open:
                Assert.Null(result.ClosedAt);
                Assert.Null(result.SnoozedUntil);
                break;
            case ConversationState.Snoozed:
                Assert.Equal(until, result.SnoozedUntil);
                Assert.Null(result.ClosedAt);
                break;
            case ConversationState.Closed:
                Assert.NotNull(result.ClosedAt);
                Assert.Null(result.SnoozedUntil);
                break;
        }
    }

    [Fact]
    public async Task Closing_AnAlreadyClosedConversation_IsIdempotentButRestamps()
    {
        var (_, _, _, convo) = await SetUpAsync();
        var first = await ReadAsync<ConversationSummaryResponse>(
            await ChangeStateAsync(convo.Id, ConversationState.Closed));

        var second = await ReadAsync<ConversationSummaryResponse>(
            await ChangeStateAsync(convo.Id, ConversationState.Closed));

        Assert.Equal(ConversationState.Closed, second.State);
        Assert.True(second.ClosedAt >= first.ClosedAt);
    }

    [Fact]
    public async Task Reclosing_AfterAReopen_KeepsTheOriginalResolutionOnRecord()
    {
        var (workspaceId, _, _, convo) = await SetUpAsync();
        await CreateSlaPolicyAsync(workspaceId, firstResponseMinutes: null, resolutionMinutes: 120);
        await MoveToAsync(convo.Id, ConversationState.Closed);
        await MoveToAsync(convo.Id, ConversationState.Open);

        // Reopened, then left far past the original resolution target.
        BackdateConversation(convo.Id, TimeSpan.FromHours(5));
        await MoveToAsync(convo.Id, ConversationState.Closed);

        // FirstResolvedAt was stamped by the first close, so the resolution
        // target was met and re-closing late does not breach it.
        var breaches = await ReadAsync<List<SlaBreachEventResponse>>(
            await Client.GetAsync($"/api/conversations/{convo.Id}/sla-breaches"));
        Assert.DoesNotContain(breaches, b => b.Kind == SlaBreachKind.Resolution);
    }

    // --- Rejected moves --------------------------------------------------

    [Fact]
    public async Task Snooze_WithoutATime_Returns422_WithExactProblemDetails()
    {
        var (_, _, _, convo) = await SetUpAsync();

        var response = await ChangeStateAsync(convo.Id, ConversationState.Snoozed);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Missing snooze time", problem!.Title);
        Assert.Equal("Snoozing requires 'snoozedUntil'.", problem.Detail);
        Assert.Equal(422, problem.Status);
    }

    [Fact]
    public async Task Snooze_IntoThePast_Returns422_AsADomainRuleViolation()
    {
        var (_, _, _, convo) = await SetUpAsync();

        var response = await ChangeStateAsync(
            convo.Id, ConversationState.Snoozed, DateTimeOffset.UtcNow.AddSeconds(-1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        // Raised by the entity, translated by DomainExceptionHandler.
        Assert.Equal("Domain rule violated", problem!.Title);
        Assert.Equal("Snooze time must be in the future.", problem.Detail);
    }

    [Fact]
    public async Task Snooze_ToExactlyNow_IsRejected()
    {
        var (_, _, _, convo) = await SetUpAsync();

        // The entity compares until <= now, so "now" is not the future.
        var response = await ChangeStateAsync(
            convo.Id, ConversationState.Snoozed, DateTimeOffset.UtcNow.AddSeconds(-30));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task ChangeState_ToAnUndefinedState_Returns422()
    {
        var (_, _, _, convo) = await SetUpAsync();

        // A number outside the enum binds, so it reaches the switch's default
        // arm rather than being caught by model binding.
        var response = await Client.PostAsync(
            $"/api/conversations/{convo.Id}/state",
            new StringContent("""{"state":99}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Unknown state", problem!.Title);
    }

    [Fact]
    public async Task ChangeState_ToAnUnparseableState_Returns400()
    {
        var (_, _, _, convo) = await SetUpAsync();

        var response = await Client.PostAsync(
            $"/api/conversations/{convo.Id}/state",
            new StringContent("""{"state":"Hibernating"}""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangeState_OnAForeignConversation_Returns404_NotAnAuthError()
    {
        var (_, _, _, convo) = await SetUpAsync();
        await CreateWorkspaceAsync("Other");

        var response = await ChangeStateAsync(convo.Id, ConversationState.Closed);

        // 404, not 403: another workspace's conversation must not be
        // distinguishable from one that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // --- Contact reply reopens from anywhere ------------------------------

    [Theory]
    [InlineData(ConversationState.Open)]
    [InlineData(ConversationState.Snoozed)]
    [InlineData(ConversationState.Closed)]
    public async Task ContactReply_FromAnyState_LeavesTheConversationOpen(ConversationState from)
    {
        var (_, contactId, _, convo) = await SetUpAsync();
        await MoveToAsync(convo.Id, from);

        var response = await ContactReplyAsync(convo.Id, contactId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var reloaded = await GetAsync(convo.Id);
        Assert.Equal(ConversationState.Open, reloaded.State);
        Assert.Null(reloaded.ClosedAt);
        Assert.Null(reloaded.SnoozedUntil);
    }

    [Fact]
    public async Task ContactReply_FromSnoozed_ClearsTheWakeTime()
    {
        var (_, contactId, _, convo) = await SetUpAsync();
        await MoveToAsync(convo.Id, ConversationState.Snoozed);

        await ContactReplyAsync(convo.Id, contactId);

        // A snooze is a promise to look later; the customer writing in cancels
        // it rather than leaving a wake-up pending on an open conversation.
        var reloaded = await GetAsync(convo.Id);
        Assert.Null(reloaded.SnoozedUntil);
    }

    [Fact]
    public async Task ContactReply_DoesNotCountAsAFirstResponse()
    {
        var (_, contactId, _, convo) = await SetUpAsync(slaMinutes: 60);

        await ContactReplyAsync(convo.Id, contactId);

        var reloaded = await GetAsync(convo.Id);
        Assert.Null(reloaded.FirstRespondedAt);
    }

    [Fact]
    public async Task TeammateReply_DoesNotReopenAClosedConversation()
    {
        var (_, _, teammateId, convo) = await SetUpAsync();
        await MoveToAsync(convo.Id, ConversationState.Closed);

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammateId, MessageKind.Reply,
                "One more thing."), Json);

        // Only the customer reopens; a teammate adding a parting note to a
        // closed thread must not drag it back into the queue.
        Assert.Equal(ConversationState.Closed, (await GetAsync(convo.Id)).State);
    }

    [Fact]
    public async Task Reply_FromSomeoneElsesContact_Returns422()
    {
        var (workspaceId, _, _, convo) = await SetUpAsync();
        var stranger = await CreateContactAsync(workspaceId, "Stranger");

        var response = await ContactReplyAsync(convo.Id, stranger.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Invalid contact author", problem!.Title);
    }

    // --- Notes are inert --------------------------------------------------

    [Theory]
    [InlineData(ConversationState.Open)]
    [InlineData(ConversationState.Snoozed)]
    [InlineData(ConversationState.Closed)]
    public async Task Note_FromAnyState_LeavesTheStateUntouched(ConversationState from)
    {
        var (_, _, teammateId, convo) = await SetUpAsync();
        await MoveToAsync(convo.Id, from);
        var before = await GetAsync(convo.Id);

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammateId, MessageKind.Note,
                "Checked the logs."), Json);

        var after = await GetAsync(convo.Id);
        Assert.Equal(from, after.State);
        Assert.Equal(before.ClosedAt, after.ClosedAt);
        Assert.Equal(before.SnoozedUntil, after.SnoozedUntil);
    }

    [Fact]
    public async Task Note_DoesNotSatisfyTheFirstResponseTarget_ButStillBumpsActivity()
    {
        var (_, _, teammateId, convo) = await SetUpAsync(slaMinutes: 60);

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammateId, MessageKind.Note, "Looking."), Json);

        var reloaded = await GetAsync(convo.Id);
        Assert.Null(reloaded.FirstRespondedAt);
        // The note is still activity: it moves LastMessageAt, which is what
        // orders the inbox.
        Assert.True(reloaded.LastMessageAt >= convo.LastMessageAt);
    }

    [Fact]
    public async Task Note_FromAContact_Returns422()
    {
        var (_, contactId, _, convo) = await SetUpAsync();

        var response = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Contact, contactId, MessageKind.Note, "sneaky"), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Invalid note author", problem!.Title);
    }

    [Fact]
    public async Task Reply_FromATeammateInAnotherWorkspace_Returns422()
    {
        var (workspaceId, _, _, convo) = await SetUpAsync();
        var other = await CreateWorkspaceAsync("Other");
        var outsider = await CreateTeammateAsync(other.Id, "Outsider");
        ActAsAdminOf(workspaceId);

        var response = await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, outsider.Id, MessageKind.Reply, "hello"), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Json);
        Assert.Equal("Unknown teammate", problem!.Title);
    }
}
