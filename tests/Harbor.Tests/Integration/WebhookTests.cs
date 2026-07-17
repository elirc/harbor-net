using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harbor.Api.Contracts;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Tests.Integration;

/// <summary>Webhook subscriptions, event publication, signing, and delivery retry.</summary>
public class WebhookTests : ApiTestBase, IDisposable
{
    public WebhookTests(HarborApiFactory factory) : base(factory) => factory.WebhookSender.Reset();

    public void Dispose() => Factory.WebhookSender.Reset();

    private static readonly WebhookEventType[] AllEvents =
    [
        WebhookEventType.ConversationCreated,
        WebhookEventType.ConversationAssigned,
        WebhookEventType.ConversationClosed,
        WebhookEventType.MessageCreated,
    ];

    private async Task<WebhookCreatedResponse> SubscribeAsync(
        Guid workspaceId, params WebhookEventType[] events)
    {
        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspaceId}/webhooks",
            new CreateWebhookRequest("https://example.test/hooks",
                events.Length == 0 ? AllEvents : events),
            Json);
        return await ReadAsync<WebhookCreatedResponse>(response);
    }

    private async Task<List<WebhookDeliveryResponse>> DeliveriesAsync(Guid subscriptionId) =>
        await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.GetAsync($"/api/webhooks/{subscriptionId}/deliveries"));

    private async Task<List<WebhookDeliveryResponse>> DispatchAsync(Guid workspaceId) =>
        await ReadAsync<List<WebhookDeliveryResponse>>(
            await Client.PostAsync($"/api/workspaces/{workspaceId}/webhooks/dispatch", null));

    /// <summary>Makes every pending delivery due, standing in for elapsed backoff.</summary>
    private void MakeDeliveriesDue(Guid workspaceId) =>
        Factory.WithDb(db =>
        {
            foreach (var delivery in db.WebhookDeliveries
                         .Where(d => d.WorkspaceId == workspaceId
                             && d.Status == WebhookDeliveryStatus.Pending))
            {
                delivery.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            }

            db.SaveChanges();
        });

    [Fact]
    public async Task Create_ReturnsSecretOnce_AndNeverAgain()
    {
        var workspace = await CreateWorkspaceAsync();

        var created = await SubscribeAsync(workspace.Id);
        var fetched = await ReadAsync<WebhookResponse>(
            await Client.GetAsync($"/api/webhooks/{created.Id}"));
        var listed = await ReadAsync<List<WebhookResponse>>(
            await Client.GetAsync($"/api/workspaces/{workspace.Id}/webhooks"));

        Assert.StartsWith("whsec_", created.Secret);
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal(4, fetched.Events.Count);
        // The response type has no secret to leak.
        var json = await (await Client.GetAsync($"/api/webhooks/{created.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(created.Secret, json);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.Single(listed);
    }

    [Fact]
    public async Task StartingAConversation_QueuesCreatedEvent_WithDottedName()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var delivery = Assert.Single(await DeliveriesAsync(subscription.Id));
        Assert.Equal(WebhookEventType.ConversationCreated, delivery.EventType);
        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);

        using var payload = JsonDocument.Parse(delivery.Payload);
        Assert.Equal("conversation.created", payload.RootElement.GetProperty("event").GetString());
        Assert.Equal(workspace.Id, payload.RootElement.GetProperty("workspaceId").GetGuid());
        Assert.Equal(convo.Id, payload.RootElement.GetProperty("data").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Message_Close_AndAssign_EachQueueTheirEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/messages",
            new AddMessageRequest(AuthorType.Teammate, teammate.Id, MessageKind.Reply, "Hi"), Json);
        await AssignAsync(convo.Id, teammate.Id);
        await Client.PostAsJsonAsync($"/api/conversations/{convo.Id}/state",
            new ChangeStateRequest(ConversationState.Closed, null), Json);

        var events = (await DeliveriesAsync(subscription.Id)).Select(d => d.EventType).ToList();

        Assert.Contains(WebhookEventType.ConversationCreated, events);
        Assert.Contains(WebhookEventType.MessageCreated, events);
        Assert.Contains(WebhookEventType.ConversationAssigned, events);
        Assert.Contains(WebhookEventType.ConversationClosed, events);
    }

    [Fact]
    public async Task AutoAssignment_AlsoQueuesAssignedEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationAssigned);
        var inbox = await CreateInboxAsync(workspace.Id, autoAssign: true);
        var contact = await CreateContactAsync(workspace.Id);

        // The round-robin assigns the bootstrap admin at creation time.
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.NotNull(convo.AssignedTeammateId);
        var delivery = Assert.Single(await DeliveriesAsync(subscription.Id));
        Assert.Equal(WebhookEventType.ConversationAssigned, delivery.EventType);
    }

    [Fact]
    public async Task Unassigning_DoesNotQueueAnAssignedEvent()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationAssigned);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        var teammate = await CreateTeammateAsync(workspace.Id);
        var convo = await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        await AssignAsync(convo.Id, teammate.Id);
        await AssignAsync(convo.Id);

        Assert.Single(await DeliveriesAsync(subscription.Id));
    }

    [Fact]
    public async Task OnlySubscribedEvents_AreQueued()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationClosed);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);

        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Empty(await DeliveriesAsync(subscription.Id));
    }

    [Fact]
    public async Task InactiveSubscription_IsNotPublishedTo()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await Client.PutAsJsonAsync($"/api/webhooks/{subscription.Id}",
            new UpdateWebhookRequest("https://example.test/hooks", AllEvents, false), Json);

        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        Assert.Empty(await DeliveriesAsync(subscription.Id));
    }

    [Fact]
    public async Task Dispatch_SendsSignedPayload_AndMarksSucceeded()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        var attempted = await DispatchAsync(workspace.Id);

        var delivery = Assert.Single(attempted);
        Assert.Equal(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(200, delivery.ResponseStatusCode);
        Assert.NotNull(delivery.DeliveredAt);

        var sent = Assert.Single(Factory.WebhookSender.Sent);
        Assert.Equal("https://example.test/hooks", sent.Url);
        Assert.Equal(subscription.Secret, sent.Secret);
        Assert.Equal(delivery.Payload, sent.Payload);
    }

    [Fact]
    public async Task Dispatch_DoesNotResendSucceededDeliveries()
    {
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        await DispatchAsync(workspace.Id);
        var second = await DispatchAsync(workspace.Id);

        Assert.Empty(second);
        Assert.Single(Factory.WebhookSender.Sent);
    }

    [Fact]
    public async Task Dispatch_FailedAttempt_SchedulesRetryWithBackoff()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        Factory.WebhookSender.Respond = _ => WebhookSendResult.Rejected(500);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        await DispatchAsync(workspace.Id);

        var delivery = Assert.Single(await DeliveriesAsync(subscription.Id));
        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(500, delivery.ResponseStatusCode);
        Assert.True(delivery.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Dispatch_SkipsDeliveriesThatAreNotDueYet()
    {
        var workspace = await CreateWorkspaceAsync();
        await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        Factory.WebhookSender.Respond = _ => WebhookSendResult.Rejected(500);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await DispatchAsync(workspace.Id);

        // Backoff has not elapsed, so a second drain finds nothing due.
        var second = await DispatchAsync(workspace.Id);

        Assert.Empty(second);
        Assert.Single(Factory.WebhookSender.Sent);
    }

    [Fact]
    public async Task Dispatch_RetriesUntilAttemptsAreExhausted_ThenFails()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        Factory.WebhookSender.Respond = _ => WebhookSendResult.Unreachable("connection refused");
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);

        for (var i = 0; i < WebhookDelivery.MaxAttempts; i++)
        {
            MakeDeliveriesDue(workspace.Id);
            await DispatchAsync(workspace.Id);
        }

        var delivery = Assert.Single(await DeliveriesAsync(subscription.Id));
        Assert.Equal(WebhookDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(WebhookDelivery.MaxAttempts, delivery.AttemptCount);
        Assert.Contains("connection refused", delivery.Error);
        Assert.Equal(WebhookDelivery.MaxAttempts, Factory.WebhookSender.Sent.Count);

        // A dead delivery is never attempted again.
        MakeDeliveriesDue(workspace.Id);
        Assert.Empty(await DispatchAsync(workspace.Id));
    }

    [Fact]
    public async Task Dispatch_RecoversWhenTheEndpointComesBack()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        Factory.WebhookSender.Respond = _ => WebhookSendResult.Rejected(503);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await DispatchAsync(workspace.Id);

        Factory.WebhookSender.Respond = _ => WebhookSendResult.Ok(200);
        MakeDeliveriesDue(workspace.Id);
        await DispatchAsync(workspace.Id);

        var delivery = Assert.Single(await DeliveriesAsync(subscription.Id));
        Assert.Equal(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Null(delivery.Error);
    }

    [Fact]
    public async Task Dispatch_SkipsDeactivatedSubscriptions_WithoutBurningAttempts()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);
        var inbox = await CreateInboxAsync(workspace.Id);
        var contact = await CreateContactAsync(workspace.Id);
        await StartConversationAsync(workspace.Id, inbox.Id, contact.Id);
        await Client.PutAsJsonAsync($"/api/webhooks/{subscription.Id}",
            new UpdateWebhookRequest("https://example.test/hooks", AllEvents, false), Json);

        await DispatchAsync(workspace.Id);

        var delivery = Assert.Single(await DeliveriesAsync(subscription.Id));
        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Empty(Factory.WebhookSender.Sent);
    }

    [Fact]
    public async Task Update_ChangesSubscribedEvents()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id, WebhookEventType.ConversationCreated);

        var updated = await ReadAsync<WebhookResponse>(
            await Client.PutAsJsonAsync($"/api/webhooks/{subscription.Id}",
                new UpdateWebhookRequest("https://example.test/other",
                    [WebhookEventType.MessageCreated, WebhookEventType.ConversationClosed], true),
                Json));

        Assert.Equal("https://example.test/other", updated.Url);
        Assert.Equal(
            [WebhookEventType.ConversationClosed, WebhookEventType.MessageCreated],
            updated.Events);
    }

    [Fact]
    public async Task Delete_RemovesTheSubscription()
    {
        var workspace = await CreateWorkspaceAsync();
        var subscription = await SubscribeAsync(workspace.Id);

        var deleted = await Client.DeleteAsync($"/api/webhooks/{subscription.Id}");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.GetAsync($"/api/webhooks/{subscription.Id}")).StatusCode);
    }

    [Fact]
    public async Task Create_WithInvalidUrl_Returns400()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/webhooks",
            new CreateWebhookRequest("not-a-url", [WebhookEventType.MessageCreated]), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithNoEvents_Returns400()
    {
        var workspace = await CreateWorkspaceAsync();

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/webhooks",
            new CreateWebhookRequest("https://example.test/hooks", []), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Webhooks_AreAdminOnly()
    {
        var workspace = await CreateWorkspaceAsync();
        var agent = await CreateTeammateAsync(workspace.Id);
        ActAs(agent.ApiKey);

        var response = await Client.PostAsJsonAsync(
            $"/api/workspaces/{workspace.Id}/webhooks",
            new CreateWebhookRequest("https://example.test/hooks", AllEvents), Json);
        var listed = await Client.GetAsync($"/api/workspaces/{workspace.Id}/webhooks");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
    }

    [Fact]
    public async Task Webhooks_ForForeignWorkspace_Return403()
    {
        var other = await CreateWorkspaceAsync("Other");
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/workspaces/{other.Id}/webhooks");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ForeignSubscription_Returns404()
    {
        var other = await CreateWorkspaceAsync("Other");
        var subscription = await SubscribeAsync(other.Id);
        await CreateWorkspaceAsync();

        var response = await Client.GetAsync($"/api/webhooks/{subscription.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
