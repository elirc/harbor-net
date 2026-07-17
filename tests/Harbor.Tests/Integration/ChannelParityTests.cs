using System.Net.Http.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;

namespace Harbor.Tests.Integration;

/// <summary>
/// Chat and email must be treated identically at the moment a conversation
/// begins.
///
/// Both channels go through ConversationStarter, which is the single creation
/// path precisely so neither can quietly skip SLA stamping, auto-assignment or
/// its webhooks. <see cref="EmailChannelTests"/> checks that email works; these
/// check that it works *the same*, by running one scenario down both channels
/// and comparing the results. A second creation path added for some future
/// channel — or an ingestion shortcut — shows up here as a divergence.
/// </summary>
public class ChannelParityTests : ApiTestBase, IDisposable
{
    public ChannelParityTests(HarborApiFactory factory) : base(factory) =>
        factory.WebhookSender.Reset();

    public void Dispose()
    {
        Factory.WebhookSender.Reset();
        GC.SuppressFinalize(this);
    }

    private const string InboxAddress = "support@acme.test";

    /// <summary>
    /// One workspace whose inbox has every start-time rule switched on —
    /// auto-assign, an SLA policy, and a webhook subscription — so anything a
    /// channel forgets to do is visible.
    /// </summary>
    private async Task<(WorkspaceResponse Workspace, InboxResponse Inbox, Guid AgentId,
        WebhookCreatedResponse Subscription)> SetUpAsync()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await ReadAsync<InboxResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/inboxes",
                new CreateInboxRequest("Support", null, true, InboxAddress), Json));
        await CreateSlaPolicyAsync(
            workspace.Id, inboxId: inbox.Id, firstResponseMinutes: 30, resolutionMinutes: 240);

        // Park the bootstrap admin so the rotation has exactly one candidate
        // and both channels must land on the same person.
        var admin = await ReadAsync<List<TeammateResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/teammates"));
        await SetAvailabilityAsync(admin.Single().Id, TeammateAvailability.Away);
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");

        var subscription = await ReadAsync<WebhookCreatedResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/webhooks",
                new CreateWebhookRequest("https://example.test/hooks",
                    [
                        WebhookEventType.ConversationCreated,
                        WebhookEventType.ConversationAssigned,
                        WebhookEventType.MessageCreated,
                    ]),
                Json));

        return (workspace, inbox, agent.Id, subscription);
    }

    private async Task<ConversationDetailResponse> GetAsync(Guid id) =>
        await ReadAsync<ConversationDetailResponse>(await Client.GetAsync($"/api/conversations/{id}"));

    private async Task<InboundEmailResponse> IngestAsync(
        Guid workspaceId, string from = "jane@example.test", string subject = "Cannot log in",
        string messageId = "<first@mail.test>") =>
        await ReadAsync<InboundEmailResponse>(
            await Client.PostAsJsonAsync(
                $"/api/workspaces/{workspaceId}/email/inbound",
                new InboundEmailRequest(
                    from, "Jane Doe", InboxAddress, subject, "Something is broken.", messageId),
                Json));

    /// <summary>Every delivery queued for the subscription, keyed by id.</summary>
    private async Task<Dictionary<Guid, WebhookEventType>> DeliveriesForAsync(Guid subscriptionId) =>
        (await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{subscriptionId}/deliveries")))
        .ToDictionary(d => d.Id, d => d.EventType);

    /// <summary>The event types on a set of deliveries, as a sorted multiset.</summary>
    private static List<WebhookEventType> Sorted(IEnumerable<WebhookEventType> events) =>
        events.OrderBy(e => e.ToString()).ToList();

    // --- The two channels line up -----------------------------------------

    [Fact]
    public async Task EmailAndChat_ProduceEquivalentConversations_DifferingOnlyInChannel()
    {
        var (workspace, inbox, agentId, _) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Jane Doe", "jane@example.test");

        var chat = await StartConversationAsync(
            workspace.Id, inbox.Id, contact.Id, "Cannot log in", "Something is broken.");
        var emailed = await GetAsync((await IngestAsync(workspace.Id)).ConversationId);

        // Everything ConversationStarter is responsible for, on both.
        Assert.Equal(chat.SlaPolicyId, emailed.SlaPolicyId);
        Assert.NotNull(emailed.SlaPolicyId);
        Assert.Equal(agentId, chat.AssignedTeammateId);
        Assert.Equal(agentId, emailed.AssignedTeammateId);
        Assert.Equal(chat.State, emailed.State);
        Assert.Equal(chat.Priority, emailed.Priority);
        Assert.Equal(chat.Subject, emailed.Subject);
        Assert.Equal(chat.ContactId, emailed.ContactId);
        Assert.Equal(chat.InboxId, emailed.InboxId);

        // The SLA targets are the same distance from each conversation's own
        // creation, which is the parity that matters.
        Assert.Equal(chat.CreatedAt.AddMinutes(30), chat.FirstResponseDueAt);
        Assert.Equal(emailed.CreatedAt.AddMinutes(30), emailed.FirstResponseDueAt);
        Assert.Equal(chat.CreatedAt.AddMinutes(240), chat.ResolutionDueAt);
        Assert.Equal(emailed.CreatedAt.AddMinutes(240), emailed.ResolutionDueAt);

        // The one intended difference.
        Assert.Equal(MessageChannel.Chat, chat.Channel);
        Assert.Equal(MessageChannel.Email, emailed.Channel);
    }

    [Fact]
    public async Task BothChannels_QueueTheSameEvents()
    {
        var (workspace, inbox, _, subscription) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Chatty", "chat@example.test");

        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");
        var afterChat = await DeliveriesForAsync(subscription.Id);

        await IngestAsync(workspace.Id);
        var afterBoth = await DeliveriesForAsync(subscription.Id);
        // The email's events are the deliveries that were not there after chat —
        // identified by id, since sorting the combined list would interleave the
        // two channels and lose which came from where.
        var fromEmail = afterBoth
            .Where(kv => !afterChat.ContainsKey(kv.Key))
            .Select(kv => kv.Value);

        // conversation.created and conversation.assigned, both times.
        Assert.Equal(
            [WebhookEventType.ConversationAssigned, WebhookEventType.ConversationCreated],
            Sorted(afterChat.Values));
        Assert.Equal(Sorted(afterChat.Values), Sorted(fromEmail));
    }

    [Fact]
    public async Task BothChannels_RecordAnAutoAssignmentEvent()
    {
        var (workspace, inbox, agentId, _) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Chatty", "chat@example.test");
        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");
        var emailed = await IngestAsync(workspace.Id);

        var chatEvents = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{chat.Id}/assignment-events"));
        var emailEvents = await ReadAsync<List<AssignmentEventResponse>>(
            await Client.GetAsync($"/api/conversations/{emailed.ConversationId}/assignment-events"));

        foreach (var events in new[] { chatEvents, emailEvents })
        {
            var single = Assert.Single(events);
            Assert.Equal(AssignmentKind.Auto, single.Kind);
            Assert.Null(single.ActorTeammateId);
            Assert.Equal(agentId, single.ToTeammateId);
        }
    }

    [Fact]
    public async Task BothChannels_ShareTheRoundRobinRotation()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await ReadAsync<InboxResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/inboxes",
                new CreateInboxRequest("Support", null, true, InboxAddress), Json));
        var admin = await ReadAsync<List<TeammateResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/teammates"));
        await SetAvailabilityAsync(admin.Single().Id, TeammateAvailability.Away);
        var first = await CreateTeammateAsync(workspace.Id, "First");
        var second = await CreateTeammateAsync(workspace.Id, "Second");
        var contact = await CreateContactAsync(workspace.Id, "Chatty", "chat@example.test");

        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");
        var emailed = await GetAsync((await IngestAsync(workspace.Id)).ConversationId);

        // One rotation, not one per channel: an emailed conversation takes the
        // next turn rather than restarting the cycle.
        Assert.Equal(first.Id, chat.AssignedTeammateId);
        Assert.Equal(second.Id, emailed.AssignedTeammateId);
    }

    [Fact]
    public async Task BothChannels_ConsumeTheSameCapacity()
    {
        var workspace = await CreateWorkspaceAsync();
        var inbox = await ReadAsync<InboxResponse>(
            await Client.PostAsJsonAsync($"/api/workspaces/{workspace.Id}/inboxes",
                new CreateInboxRequest("Support", null, true, InboxAddress), Json));
        var admin = await ReadAsync<List<TeammateResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/teammates"));
        await SetAvailabilityAsync(admin.Single().Id, TeammateAvailability.Away);
        var agent = await CreateTeammateAsync(workspace.Id, "Solo");
        await SetAvailabilityAsync(agent.Id, TeammateAvailability.Available, capacityLimit: 1);
        var contact = await CreateContactAsync(workspace.Id, "Chatty", "chat@example.test");

        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");
        var emailed = await GetAsync((await IngestAsync(workspace.Id)).ConversationId);

        // The chat conversation filled the only slot, so the emailed one waits.
        Assert.Equal(agent.Id, chat.AssignedTeammateId);
        Assert.Null(emailed.AssignedTeammateId);
    }

    [Fact]
    public async Task BothChannels_BreachTheSameWay()
    {
        var (workspace, inbox, _, _) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Chatty", "chat@example.test");
        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");
        var emailed = await IngestAsync(workspace.Id);

        BackdateConversation(chat.Id, TimeSpan.FromHours(5));
        BackdateConversation(emailed.ConversationId, TimeSpan.FromHours(5));
        await Client.PostAsync($"/api/workspaces/{workspace.Id}/sla/evaluate", null);

        var chatBreaches = await ReadAsync<List<SlaBreachEventResponse>>(
            await Client.GetAsync($"/api/conversations/{chat.Id}/sla-breaches"));
        var emailBreaches = await ReadAsync<List<SlaBreachEventResponse>>(
            await Client.GetAsync($"/api/conversations/{emailed.ConversationId}/sla-breaches"));

        Assert.Equal(
            chatBreaches.Select(b => b.Kind).OrderBy(k => k.ToString()),
            emailBreaches.Select(b => b.Kind).OrderBy(k => k.ToString()));
        Assert.Equal(2, chatBreaches.Count);
    }

    // --- Channel tracking ---------------------------------------------------

    [Fact]
    public async Task EmailIngestion_StampsTheChannel_OnBothConversationAndMessage()
    {
        var (workspace, _, _, _) = await SetUpAsync();

        var result = await IngestAsync(workspace.Id);

        var convo = await GetAsync(result.ConversationId);
        Assert.Equal(MessageChannel.Email, convo.Channel);
        var message = Assert.Single(convo.Messages);
        Assert.Equal(MessageChannel.Email, message.Channel);
        Assert.Equal("<first@mail.test>", message.EmailMessageId);
        Assert.Equal(AuthorType.Contact, message.AuthorType);
    }

    [Fact]
    public async Task AChatConversation_StaysChat_EvenOnAnInboxThatReceivesMail()
    {
        var (workspace, inbox, _, _) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Chatty", "chat@example.test");

        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");

        // The inbox has an address, but this conversation did not arrive by
        // mail — the channel records how it actually reached us.
        var convo = await GetAsync(chat.Id);
        Assert.Equal(MessageChannel.Chat, convo.Channel);
        Assert.Null(Assert.Single(convo.Messages).EmailMessageId);
        Assert.Equal(MessageChannel.Chat, Assert.Single(convo.Messages).Channel);
    }

    [Fact]
    public async Task TheChannelFilter_SeparatesTheTwo_OnOneInbox()
    {
        var (workspace, inbox, _, _) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Chatty", "chat@example.test");
        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");
        var emailed = await IngestAsync(workspace.Id);

        var byChat = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?channel=Chat"));
        var byEmail = await ReadAsync<List<ConversationSummaryResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/conversations?channel=Email"));

        Assert.Equal(chat.Id, Assert.Single(byChat).Id);
        Assert.Equal(emailed.ConversationId, Assert.Single(byEmail).Id);
    }

    [Fact]
    public async Task AnEmailedReply_ToAChatConversation_DoesNotChangeItsChannel()
    {
        var (workspace, inbox, _, _) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Jane Doe", "jane@example.test");
        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");

        // A contact reply arriving over chat on the same conversation.
        await Client.PostAsJsonAsync($"/api/conversations/{chat.Id}/messages",
            new AddMessageRequest(AuthorType.Contact, contact.Id, MessageKind.Reply, "Any news?"),
            Json);

        // Channel is a property of how the conversation started, and stays put.
        Assert.Equal(MessageChannel.Chat, (await GetAsync(chat.Id)).Channel);
    }

    [Fact]
    public async Task EveryChannel_UsesTheSameStateMachine()
    {
        var (workspace, inbox, _, _) = await SetUpAsync();
        var contact = await CreateContactAsync(workspace.Id, "Jane Doe", "jane@example.test");
        var chat = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "chat");
        var emailed = await IngestAsync(workspace.Id);

        foreach (var id in new[] { chat.Id, emailed.ConversationId })
        {
            await Client.PostAsJsonAsync($"/api/conversations/{id}/state",
                new ChangeStateRequest(ConversationState.Closed, null), Json);
            Assert.Equal(ConversationState.Closed, (await GetAsync(id)).State);
        }

        // A contact writing back reopens either one.
        await Client.PostAsJsonAsync($"/api/conversations/{chat.Id}/messages",
            new AddMessageRequest(AuthorType.Contact, contact.Id, MessageKind.Reply, "Still broken."),
            Json);
        await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/email/inbound",
            new InboundEmailRequest("jane@example.test", "Jane Doe", InboxAddress,
                "Re: Cannot log in", "Still broken.", "<second@mail.test>", "<first@mail.test>"),
            Json);

        Assert.Equal(ConversationState.Open, (await GetAsync(chat.Id)).State);
        Assert.Equal(ConversationState.Open, (await GetAsync(emailed.ConversationId)).State);
    }
}
