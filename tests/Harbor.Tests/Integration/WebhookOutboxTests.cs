using System.Net.Http.Json;
using System.Text.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Tests.Integration;

/// <summary>
/// The transactional-outbox property itself: an event exists exactly when the
/// change that caused it exists.
///
/// <see cref="WebhookTests"/> covers subscriptions, dispatch and retry. These
/// go after the invariant underneath all of it — no event without its change,
/// no change without its event — which is the thing that makes the event log
/// trustworthy and the thing an inline HTTP call would quietly break.
/// </summary>
public class WebhookOutboxTests : ApiTestBase, IDisposable
{
    public WebhookOutboxTests(HarborApiFactory factory) : base(factory) =>
        factory.WebhookSender.Reset();

    public void Dispose()
    {
        Factory.WebhookSender.Reset();
        GC.SuppressFinalize(this);
    }

    private static readonly WebhookEventType[] AllEvents =
    [
        WebhookEventType.ConversationCreated,
        WebhookEventType.ConversationAssigned,
        WebhookEventType.ConversationClosed,
        WebhookEventType.MessageCreated,
    ];

    /// <summary>A DbContext scope that async test bodies can await inside.</summary>
    private async Task WithDbAsync(Func<HarborDbContext, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<HarborDbContext>());
    }

    private async Task<WebhookCreatedResponse> SubscribeAsync(
        Guid workspaceId, params WebhookEventType[] events) =>
        await ReadAsync<WebhookCreatedResponse>(
            await Client.PostAsJsonAsync(
                $"/api/workspaces/{workspaceId}/webhooks",
                new CreateWebhookRequest(
                    "https://example.test/hooks", events.Length == 0 ? AllEvents : events),
                Json));

    private int DeliveryCount(Guid workspaceId)
    {
        var count = 0;
        Factory.WithDb(db => count = db.WebhookDeliveries.Count(d => d.WorkspaceId == workspaceId));
        return count;
    }

    // --- No event without its change --------------------------------------

    [Fact]
    public async Task PublishedEvent_ThatIsNeverSaved_NeverReachesTheOutbox()
    {
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);

        await WithDbAsync(async db =>
        {
            // Publishing only stages rows in the change tracker.
            var queued = await Webhooks.PublishAsync(
                db, workspace.Id, WebhookEventType.ConversationCreated,
                new { id = Guid.NewGuid() }, DateTimeOffset.UtcNow);
            Assert.Single(queued);
            // Deliberately no SaveChanges: the caller's work fell over here.
        });

        // Nothing was committed, so nothing is owed to the subscriber.
        Assert.Equal(0, DeliveryCount(workspace.Id));
    }

    [Fact]
    public async Task AChange_AndItsEvent_AreCommittedByTheSameTransaction()
    {
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var conversationId = Guid.NewGuid();

        await WithDbAsync(async db =>
        {
            using var transaction = await db.Database.BeginTransactionAsync();
            db.Conversations.Add(new Conversation
            {
                Id = conversationId,
                WorkspaceId = workspace.Id,
                InboxId = inbox.Id,
                ContactId = contact.Id,
                Subject = "Rolled back",
            });
            await Webhooks.PublishAsync(
                db, workspace.Id, WebhookEventType.ConversationCreated,
                new { id = conversationId }, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();

            // Both are real inside the transaction...
            Assert.Equal(1, await db.WebhookDeliveries.CountAsync(d => d.WorkspaceId == workspace.Id));

            // ...and the transaction is what decides whether either happened.
            await transaction.RollbackAsync();
        });

        // The conversation never existed, so neither does the event announcing
        // it. An inline send would already have told the subscriber otherwise.
        Factory.WithDb(db =>
            Assert.False(db.Conversations.Any(c => c.Id == conversationId)));
        Assert.Equal(0, DeliveryCount(workspace.Id));
    }

    [Fact]
    public async Task ARejectedStateChange_QueuesNoEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.ConversationClosed);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        // Rejected: snoozing into the past.
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Snoozed, DateTimeOffset.UtcNow.AddDays(-1)), Json);

        Assert.Equal(0, DeliveryCount(workspace.Id));
    }

    [Fact]
    public async Task ARejectedAssignment_QueuesNoAssignedEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.ConversationAssigned);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        // Rejected: a teammate from another workspace.
        var other = await CreateWorkspaceAsync("Other");
        var outsider = await CreateTeammateAsync(other.Id);
        ActAsAdminOf(workspace.Id);
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/assignment",
            new AssignConversationRequest(outsider.Id, null), Json);

        Assert.Equal(0, DeliveryCount(workspace.Id));
    }

    [Fact]
    public async Task ARejectedMessage_QueuesNoMessageEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.MessageCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        var stranger = await CreateContactAsync(workspace.Id, "Stranger");

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Contact, stranger.Id, MessageKind.Reply, "hi"), Json);

        Assert.Equal(0, DeliveryCount(workspace.Id));
    }

    // --- No change without its event --------------------------------------

    [Fact]
    public async Task EveryCommittedConversation_HasExactlyOneCreatedEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        foreach (var i in Enumerable.Range(0, 5))
        {
            await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, $"convo {i}");
        }

        var deliveries = await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{subscription.Id}/deliveries"));

        // One event per conversation: no duplicates, and none missing.
        Assert.Equal(5, deliveries.Count);
        var conversationIds = deliveries
            .Select(d => JsonDocument.Parse(d.Payload)
                .RootElement.GetProperty("data").GetProperty("id").GetGuid())
            .ToList();
        Assert.Equal(5, conversationIds.Distinct().Count());
    }

    [Fact]
    public async Task EachSubscription_GetsItsOwnCopy_OfTheSameEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        var first = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var second = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var firstDeliveries = await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{first.Id}/deliveries"));
        var secondDeliveries = await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{second.Id}/deliveries"));

        // Independent deliveries: one subscriber failing must not affect the
        // other's retry budget.
        Assert.Single(firstDeliveries);
        Assert.Single(secondDeliveries);
        Assert.NotEqual(firstDeliveries[0].Id, secondDeliveries[0].Id);
    }

    [Fact]
    public async Task AutoAssignment_CommitsBothTheCreatedAndAssignedEvents_Together()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(
            workspace.Id, WebhookEventType.ConversationCreated, WebhookEventType.ConversationAssigned);
        var inbox = await CreateInboxAsync(workspace.Id, autoAssign: true);
        var contact = await CreateContactAsync(workspace.Id);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var deliveries = await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{subscription.Id}/deliveries"));

        // Creation and its auto-assignment are one atomic act, so both events
        // land or neither does.
        Assert.NotNull(convo.AssignedTeammateId);
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, d => d.EventType == WebhookEventType.ConversationCreated);
        Assert.Contains(deliveries, d => d.EventType == WebhookEventType.ConversationAssigned);
    }

    [Fact]
    public async Task DeliveriesAreScopedToTheirWorkspace()
    {
        var other = await CreateWorkspaceAsync("Other");
        await SubscribeAsync(other.Id, WebhookEventType.ConversationCreated);
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        // The other workspace's subscriber is not told about our traffic.
        Assert.Equal(1, DeliveryCount(workspace.Id));
        Assert.Equal(0, DeliveryCount(other.Id));
    }

    [Fact]
    public async Task ThePayloadIsFrozenAtPublishTime_NotRenderedAtSendTime()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id, "Original subject");

        // The conversation moves on after the event was queued.
        await SetPriorityAsync(convo.Id, ConversationPriority.Urgent);
        await Client.PostAsync($"/api/workspaces/{workspace.Id}/webhooks/dispatch", null);

        var sent = Assert.Single(Factory.WebhookSender.Sent);
        using var payload = JsonDocument.Parse(sent.Payload);
        var data = payload.RootElement.GetProperty("data");

        // The event describes the moment it happened. Re-rendering at send
        // time would silently rewrite history — and break the signature that
        // covers these exact bytes.
        Assert.Equal("Original subject", data.GetProperty("subject").GetString());
        Assert.Equal(
            nameof(ConversationPriority.Normal), data.GetProperty("priority").GetString());
    }

    [Fact]
    public async Task DispatchSendsTheStoredBytes_Unmodified()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        await Client.PostAsync($"/api/workspaces/{workspace.Id}/webhooks/dispatch", null);

        var stored = (await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{subscription.Id}/deliveries"))).Single();
        var sent = Assert.Single(Factory.WebhookSender.Sent);
        Assert.Equal(stored.Payload, sent.Payload);
    }
}
