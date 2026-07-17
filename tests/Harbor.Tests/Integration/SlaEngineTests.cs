using System.Net;
using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>SLA policies, conversation priority, and breach detection.</summary>
public class SlaEngineTests(HarborApiFactory factory) : ApiTestBase(factory)
{
    private async Task<List<SlaBreachEventResponse>> GetBreachesAsync(Guid conversationId) =>
        await ReadAsync<List<SlaBreachEventResponse>>(
            await Client.GetAsync($"/api/conversations/{conversationId}/sla-breaches"));

    private async Task<List<SlaBreachEventResponse>> EvaluateAsync(Guid workspaceId) =>
        await ReadAsync<List<SlaBreachEventResponse>>(
            await Client.PostAsync($"/api/workspaces/{workspaceId}/sla/evaluate", null));

    [Fact]
    public async Task StartConversation_StampsTargets_FromMatchingPolicy()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var policy = await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 1_440);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Equal(policy.Id, convo.SlaPolicyId);
        Assert.Equal(convo.CreatedAt.AddMinutes(60), convo.FirstResponseDueAt);
        Assert.Equal(convo.CreatedAt.AddMinutes(1_440), convo.ResolutionDueAt);
    }

    [Fact]
    public async Task StartConversation_WithNoPolicy_FallsBackToInboxSla()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, slaMinutes: 90);
        var contact = await CreateContactAsync(workspace.Id);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Null(convo.SlaPolicyId);
        Assert.Equal(convo.CreatedAt.AddMinutes(90), convo.FirstResponseDueAt);
        Assert.Null(convo.ResolutionDueAt);
    }

    [Fact]
    public async Task StartConversation_UsesPriorityPolicy_WhenStartedUrgent()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, "Standard", firstResponseMinutes: 60);
        var urgent = await CreateSlaPolicyAsync(
            workspace.Id, "Urgent", priority: ConversationPriority.Urgent, firstResponseMinutes: 15);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/conversations",
            new StartConversationRequest(inbox.Id, contact.Id, "Site down", "Everything is down!",
                ConversationPriority.Urgent),
            Json);
        var convo = await ReadAsync<ConversationDetailResponse>(response);

        Assert.Equal(ConversationPriority.Urgent, convo.Priority);
        Assert.Equal(urgent.Id, convo.SlaPolicyId);
        Assert.Equal(convo.CreatedAt.AddMinutes(15), convo.FirstResponseDueAt);
    }

    [Fact]
    public async Task ChangePriority_ReStampsTargets_FromCreationNotNow()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, "Standard", firstResponseMinutes: 60);
        var urgent = await CreateSlaPolicyAsync(
            workspace.Id, "Urgent", priority: ConversationPriority.Urgent, firstResponseMinutes: 15);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var escalated = await ReadAsync<ConversationSummaryResponse>(
            await Client.PutAsJsonAsync($"/api/conversations/{convo.Id}/priority",
                new SetPriorityRequest(ConversationPriority.Urgent), Json));

        Assert.Equal(ConversationPriority.Urgent, escalated.Priority);
        Assert.Equal(urgent.Id, escalated.SlaPolicyId);
        // The clock still runs from creation, not from the escalation.
        Assert.Equal(convo.CreatedAt.AddMinutes(15), escalated.FirstResponseDueAt);
    }

    [Fact]
    public async Task LateFirstReply_RecordsFirstResponseBreach()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(2));

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "Sorry for the delay!"),
            Json);

        var breach = Assert.Single(await GetBreachesAsync(convo.Id));
        Assert.Equal(SlaBreachKind.FirstResponse, breach.Kind);
        // The reply itself is the moment the target was missed.
        Assert.True(breach.BreachedAt > breach.DueAt);
    }

    [Fact]
    public async Task FirstReplyInsideTarget_RecordsNoBreach()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "On it!"), Json);

        Assert.Empty(await GetBreachesAsync(convo.Id));
    }

    [Fact]
    public async Task InternalNote_DoesNotSatisfyFirstResponse()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(2));

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Note, "Looking into this."),
            Json);

        // The note leaves the conversation unanswered, so the target stays missed.
        var breach = Assert.Single(await GetBreachesAsync(convo.Id));
        Assert.Equal(SlaBreachKind.FirstResponse, breach.Kind);
        var detail = await ReadAsync<ConversationDetailResponse>(
            await Client.GetAsync($"/api/conversations/{convo.Id}"));
        Assert.Null(detail.FirstRespondedAt);
    }

    [Fact]
    public async Task LateClose_RecordsResolutionBreach()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 120);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "On it!"), Json);
        BackdateConversation(convo.Id, TimeSpan.FromHours(3));

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        var breach = Assert.Single(await GetBreachesAsync(convo.Id));
        Assert.Equal(SlaBreachKind.Resolution, breach.Kind);
    }

    [Fact]
    public async Task Evaluate_RecordsBreaches_ForConversationsSittingOverdue()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 120);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(5));

        var recorded = await EvaluateAsync(workspace.Id);

        // Nothing happened to this conversation at all, so both targets lapsed.
        Assert.Equal(2, recorded.Count);
        Assert.Contains(recorded, b => b.Kind == SlaBreachKind.FirstResponse);
        Assert.Contains(recorded, b => b.Kind == SlaBreachKind.Resolution);
    }

    [Fact]
    public async Task Evaluate_IsIdempotent()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        BackdateConversation(convo.Id, TimeSpan.FromHours(2));

        var first = await EvaluateAsync(workspace.Id);
        var second = await EvaluateAsync(workspace.Id);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Single(await GetBreachesAsync(convo.Id));
    }

    [Fact]
    public async Task Evaluate_LeavesHealthyConversationsAlone()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 120);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Empty(await EvaluateAsync(workspace.Id));
    }

    [Fact]
    public async Task SlaBreachedFilter_FindsResolutionBreaches()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60, resolutionMinutes: 120);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "Overdue");
        // Reply in time, then let only the resolution target lapse.
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "On it!"), Json);
        BackdateConversation(convo.Id, TimeSpan.FromHours(3));

        var breached = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?slaBreached=true"));

        var found = Assert.Single(breached);
        Assert.Equal(convo.Id, found.Id);
        Assert.NotNull(found.FirstRespondedAt);
    }

    [Fact]
    public async Task PriorityFilter_NarrowsToMatchingConversations()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var normal = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "Normal");
        var urgent = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "Urgent");
        await Client.PutAsJsonAsync($"/api/conversations/{urgent.Id}/priority",
            new SetPriorityRequest(ConversationPriority.Urgent), Json);

        var found = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?priority=Urgent"));

        Assert.Equal(urgent.Id, Assert.Single(found).Id);
        Assert.DoesNotContain(found, c => c.Id == normal.Id);
    }

    [Fact]
    public async Task CreatePolicy_WithNoTargets_Returns422()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/sla-policies",
            new CreateSlaPolicyRequest("Empty", null, null, null, null), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreatePolicy_WithDuplicateScope_Returns409()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, "First", inboxId: inbox.Id);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/sla-policies",
            new CreateSlaPolicyRequest("Second", inbox.Id, null, 30, null), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task CreatePolicy_ForForeignInbox_Returns422()
    {
        var other = await CreateWorkspaceAsync("Other");
        var foreignInbox = await CreateInboxAsync(other.Id);
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/sla-policies",
            new CreateSlaPolicyRequest("Cross", foreignInbox.Id, null, 60, null), Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreatePolicy_AsAgent_IsForbidden()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id);
        ActAs(agent.ApiKey);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/sla-policies",
            new CreateSlaPolicyRequest("Nope", null, null, 60, null), Json);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Policies_ListMostSpecificFirst_AndRoundTrip()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        await CreateSlaPolicyAsync(workspace.Id, "Default");
        await CreateSlaPolicyAsync(
            workspace.Id, "Inbox+priority", inboxId: inbox.Id, priority: ConversationPriority.Urgent);
        await CreateSlaPolicyAsync(workspace.Id, "Inbox", inboxId: inbox.Id);

        var policies = await ReadAsync<List<SlaPolicyResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/sla-policies"));

        Assert.Equal(["Inbox+priority", "Inbox", "Default"], policies.Select(p => p.Name));
    }

    [Fact]
    public async Task UpdatePolicy_ChangesTargets_ForNewConversations()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var policy = await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 60);

        await Client.PutAsJsonAsync($"/api/sla-policies/{policy.Id}",
            new UpdateSlaPolicyRequest("Tightened", inbox.Id, null, 10, null), Json);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Equal(convo.CreatedAt.AddMinutes(10), convo.FirstResponseDueAt);
    }

    [Fact]
    public async Task DeletePolicy_FallsBackToInboxSla()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id, slaMinutes: 90);
        var contact = await CreateContactAsync(workspace.Id);
        var policy = await CreateSlaPolicyAsync(workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 10);

        await Client.DeleteAsync($"/api/sla-policies/{policy.Id}");
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Null(convo.SlaPolicyId);
        Assert.Equal(convo.CreatedAt.AddMinutes(90), convo.FirstResponseDueAt);
    }

    [Fact]
    public async Task SlaBreaches_ForForeignConversation_Returns404()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await CreateWorkspaceAsync("Other");

        var response = await Client.GetAsync($"/api/conversations/{convo.Id}/sla-breaches");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Policy_ForForeignWorkspace_Returns403()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/workspaces/{other.Id}/sla-policies");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
