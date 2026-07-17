using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>
/// SLA behaviour through the API at its awkward edges: priority-specific
/// policies winning and re-stamping, the distinction between responded-late and
/// still-overdue, and the fact that snoozing and reopening move nothing on the
/// clock.
///
/// <see cref="SlaEngineTests"/> covers the happy paths; these cover the ones a
/// customer argues about — the escalation that puts a conversation instantly
/// past due, the reply that lands one target late but not the other, the snooze
/// that does not buy time.
/// </summary>
public class SlaEngineEdgeCaseTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<List<SlaBreachEventResponse>> BreachesAsync(Guid conversationId) =>
        await ReadAsync<List<SlaBreachEventResponse>>(
            await Client.GetAsync($"/api/conversations/{conversationId}/sla-breaches"));

    private async Task<List<SlaBreachEventResponse>> EvaluateAsync(Guid workspaceId) =>
        await ReadAsync<List<SlaBreachEventResponse>>(
            await Client.PostAsync($"/api/workspaces/{workspaceId}/sla/evaluate", null));

    private async Task ReplyAsync(Guid conversationId, Guid teammateId, string body = "On it.") =>
        (await Client.PostAsJsonAsync($"/api/conversations/{conversationId}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammateId, MessageKind.Reply, body), Json))
        .EnsureSuccessStatusCode();

    private async Task CloseAsync(Guid conversationId) =>
        (await Client.PostAsJsonAsync($"/api/conversations/{conversationId}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json)).EnsureSuccessStatusCode();

    // --- Priority-specific policies ---------------------------------------

    [Fact]
    public async Task AnUrgentPolicy_Wins_OverTheInboxWidePolicy()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, "Inbox default", inboxId: inbox.Id, firstResponseMinutes: 120);
        var urgent = await CreateSlaPolicyAsync(
            workspace.Id, "Inbox urgent", inboxId: inbox.Id,
            priority: ConversationPriority.Urgent, firstResponseMinutes: 15);

        var convo = await ReadAsync<ConversationDetailResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/conversations",
                new StartConversationRequest(inbox.Id, contact.Id, "Down", "!!",
                    ConversationPriority.Urgent), Json));

        Assert.Equal(urgent.Id, convo.SlaPolicyId);
        Assert.Equal(convo.CreatedAt.AddMinutes(15), convo.FirstResponseDueAt);
    }

    [Fact]
    public async Task Escalating_AnOldConversation_CanPutItImmediatelyPastDue()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, "Default", firstResponseMinutes: 480);
        await CreateSlaPolicyAsync(workspace.Id, "Urgent",
            priority: ConversationPriority.Urgent, firstResponseMinutes: 15);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        // 30 minutes old: inside the 8-hour default, but past a 15-minute urgent.
        BackdateConversation(convo.Id, TimeSpan.FromMinutes(30));

        await SetPriorityAsync(convo.Id, ConversationPriority.Urgent);

        // The clock runs from creation, so escalation moves the deadline into
        // the past and the breach is recorded on the spot.
        var breach = Assert.Single(await BreachesAsync(convo.Id));
        Assert.Equal(SlaBreachKind.FirstResponse, breach.Kind);
    }

    [Fact]
    public async Task Deescalating_BeforeReplying_CanPullAConversationBackInsideTarget()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, "Default", firstResponseMinutes: 480);
        await CreateSlaPolicyAsync(workspace.Id, "Urgent",
            priority: ConversationPriority.Urgent, firstResponseMinutes: 15);
        var convo = await ReadAsync<ConversationDetailResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/conversations",
                new StartConversationRequest(inbox.Id, contact.Id, "Down", "!!",
                    ConversationPriority.Urgent), Json));
        BackdateConversation(convo.Id, TimeSpan.FromMinutes(30));

        // Downgrade to Normal, which is governed by the 8-hour default.
        var normal = await SetPriorityAsync(convo.Id, ConversationPriority.Normal);

        // The new target is measured from creation (the conversation is 30 min
        // old), so an 8-hour default lands comfortably in the future — the
        // conversation is back inside target.
        Assert.True(
            normal.FirstResponseDueAt > DateTimeOffset.UtcNow.AddHours(7),
            $"expected the 8-hour target to be well in the future, was {normal.FirstResponseDueAt}");
        // No breach has been recorded yet, and the new target is comfortably away.
        // (A breach already on record would persist; none was, so none appears.)
        Assert.DoesNotContain(
            await BreachesAsync(convo.Id), b => b.Kind == SlaBreachKind.FirstResponse);
    }

    [Fact]
    public async Task ARecordedBreach_Persists_EvenAfterDeescalation()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, "Default", firstResponseMinutes: 480);
        await CreateSlaPolicyAsync(workspace.Id, "Urgent",
            priority: ConversationPriority.Urgent, firstResponseMinutes: 15);
        var convo = await ReadAsync<ConversationDetailResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/conversations",
                new StartConversationRequest(inbox.Id, contact.Id, "Down", "!!",
                    ConversationPriority.Urgent), Json));
        BackdateConversation(convo.Id, TimeSpan.FromMinutes(30));
        // Detect the urgent breach while it is urgent.
        await EvaluateAsync(workspace.Id);
        Assert.Single(await BreachesAsync(convo.Id));

        // Downgrading does not un-happen a breach that already occurred.
        await SetPriorityAsync(convo.Id, ConversationPriority.Normal);

        Assert.Single(await BreachesAsync(convo.Id));
    }

    // --- Responded-late vs still-overdue ----------------------------------

    [Fact]
    public async Task ALateFirstReply_BreachesOnTheReply_NotOnTheSweep()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(2));

        await ReplyAsync(convo.Id, teammate.Id);

        var breach = Assert.Single(await BreachesAsync(convo.Id));
        Assert.Equal(SlaBreachKind.FirstResponse, breach.Kind);
        // BreachedAt is the moment the late reply landed, which is after the due
        // time — the reply itself is what missed the target.
        Assert.True(breach.BreachedAt > breach.DueAt);
        var detail = await ReadAsync<ConversationDetailResponse>(
            await Client.GetAsync($"/api/conversations/{convo.Id}"));
        Assert.NotNull(detail.FirstRespondedAt);
    }

    [Fact]
    public async Task AnUnansweredOverdueConversation_BreachesAtSweepTime_WithNoResponse()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(2));

        var recorded = Assert.Single(await EvaluateAsync(workspace.Id));

        // Nobody replied, so BreachedAt is "now" — the moment the sweep noticed
        // — and it is still after the due time.
        Assert.Equal(SlaBreachKind.FirstResponse, recorded.Kind);
        Assert.True(recorded.BreachedAt > recorded.DueAt);
        var detail = await ReadAsync<ConversationDetailResponse>(
            await Client.GetAsync($"/api/conversations/{convo.Id}"));
        Assert.Null(detail.FirstRespondedAt);
    }

    [Fact]
    public async Task ReplyLate_ThenCloseLate_RecordsBothBreaches_Once()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 30, resolutionMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(3));

        await ReplyAsync(convo.Id, teammate.Id);
        await CloseAsync(convo.Id);
        // A redundant sweep must not double-record.
        await EvaluateAsync(workspace.Id);

        var breaches = await BreachesAsync(convo.Id);
        Assert.Equal(2, breaches.Count);
        Assert.Contains(breaches, b => b.Kind == SlaBreachKind.FirstResponse);
        Assert.Contains(breaches, b => b.Kind == SlaBreachKind.Resolution);
    }

    [Fact]
    public async Task RepliedOnTime_ButClosedLate_BreachesOnlyResolution()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 120);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        // Reply promptly, then let only the resolution target lapse.
        await ReplyAsync(convo.Id, teammate.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(3));

        await CloseAsync(convo.Id);

        var breach = Assert.Single(await BreachesAsync(convo.Id));
        Assert.Equal(SlaBreachKind.Resolution, breach.Kind);
    }

    // --- Snooze and reopen do not move the clock --------------------------

    [Fact]
    public async Task Snoozing_DoesNotStopTheFirstResponseClock()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        // Snooze for a week, then jump two hours past creation.
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Snoozed, DateTimeOffset.UtcNow.AddDays(7)), Json);
        BackdateConversation(convo.Id, TimeSpan.FromHours(2));

        // The customer is still unanswered; the snooze bought the agent no time.
        var breach = Assert.Single(await EvaluateAsync(workspace.Id));
        Assert.Equal(SlaBreachKind.FirstResponse, breach.Kind);
    }

    [Fact]
    public async Task ReopeningAResolvedConversation_DoesNotReviveTheResolutionTarget()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 240);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await ReplyAsync(convo.Id, teammate.Id);
        await CloseAsync(convo.Id);

        // Reopen and let it sit long past the original resolution target.
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Open, null), Json);
        BackdateConversation(convo.Id, TimeSpan.FromHours(10));
        await EvaluateAsync(workspace.Id);

        // Resolution is judged on the first close, which was on time, so no
        // resolution breach appears however long the reopened thread lingers.
        Assert.DoesNotContain(
            await BreachesAsync(convo.Id), b => b.Kind == SlaBreachKind.Resolution);
    }

    [Fact]
    public async Task ContactReopen_AfterAnOnTimeReply_DoesNotUndoTheFirstResponse()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await ReplyAsync(convo.Id, teammate.Id);
        await CloseAsync(convo.Id);

        // The customer writes back much later, reopening the conversation.
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Contact, contact.Id, MessageKind.Reply, "Back again"), Json);
        BackdateConversation(convo.Id, TimeSpan.FromHours(5));
        await EvaluateAsync(workspace.Id);

        // FirstRespondedAt was set on the original on-time reply and is never
        // cleared, so the reopened conversation does not retroactively breach.
        Assert.DoesNotContain(
            await BreachesAsync(convo.Id), b => b.Kind == SlaBreachKind.FirstResponse);
    }

    // --- Inbox-level fallback ----------------------------------------------

    [Fact]
    public async Task WithNoPolicy_TheInboxFirstResponseMinutes_StillBreach()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, slaMinutes: 45);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(2));

        var breach = Assert.Single(await EvaluateAsync(workspace.Id));

        // The inbox fallback has a first-response target but no resolution one.
        Assert.Equal(SlaBreachKind.FirstResponse, breach.Kind);
        Assert.Null(breach.SlaPolicyId);
    }

    [Fact]
    public async Task AnInboxWithNoSlaAtAll_NeverBreaches()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromDays(30));

        Assert.Empty(await EvaluateAsync(workspace.Id));
        Assert.Empty(await BreachesAsync(convo.Id));
    }

    [Fact]
    public async Task TheMostRecentPolicyEdit_GovernsNewConversations_NotOldOnes()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var policy = await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 120);
        var early = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "early");

        await Client.PutAsJsonAsync($"/api/sla-policies/{policy.Id}",
            new UpdateSlaPolicyRequest("Tighter", inbox.Id, null, 15, null), Json);
        var late = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "late");

        // The early conversation kept the target it was stamped with; only the
        // new one gets the tightened deadline.
        Assert.Equal(early.CreatedAt.AddMinutes(120), early.FirstResponseDueAt);
        Assert.Equal(late.CreatedAt.AddMinutes(15), late.FirstResponseDueAt);
    }
}
