using System.Net;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

public class WebhookDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static WebhookDelivery NewDelivery() => new()
    {
        SubscriptionId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        EventType = WebhookEventType.ConversationCreated,
        Payload = """{"event":"conversation.created"}""",
        CreatedAt = Now,
        NextAttemptAt = Now,
    };

    [Fact]
    public void Succeed_MarksDelivered()
    {
        var delivery = NewDelivery();

        delivery.Succeed(202, Now);

        Assert.Equal(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(Now, delivery.DeliveredAt);
        Assert.Equal(202, delivery.ResponseStatusCode);
        Assert.Null(delivery.Error);
    }

    [Fact]
    public void Fail_SchedulesExponentialBackoff()
    {
        var delivery = NewDelivery();
        var expected = new[] { 1d, 2d, 4d, 8d };

        foreach (var (minutes, attempt) in expected.Select((m, i) => (m, i + 1)))
        {
            delivery.Fail(500, "boom", Now);

            Assert.Equal(attempt, delivery.AttemptCount);
            Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
            Assert.Equal(Now.AddMinutes(minutes), delivery.NextAttemptAt);
        }
    }

    [Fact]
    public void Fail_GivesUpAfterMaxAttempts()
    {
        var delivery = NewDelivery();

        for (var i = 0; i < WebhookDelivery.MaxAttempts; i++)
        {
            delivery.Fail(500, "boom", Now);
        }

        Assert.Equal(WebhookDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(WebhookDelivery.MaxAttempts, delivery.AttemptCount);
        Assert.Null(delivery.DeliveredAt);
    }

    [Fact]
    public void Fail_ThenSucceed_ClearsTheError()
    {
        var delivery = NewDelivery();

        delivery.Fail(503, "unavailable", Now);
        delivery.Succeed(200, Now.AddMinutes(1));

        Assert.Equal(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Null(delivery.Error);
    }

    [Fact]
    public void Fail_RecordsUnreachableWithoutAStatusCode()
    {
        var delivery = NewDelivery();

        delivery.Fail(null, "Name or service not known.", Now);

        Assert.Null(delivery.ResponseStatusCode);
        Assert.Equal("Name or service not known.", delivery.Error);
    }

    [Fact]
    public void WireNames_AreTheDottedContract()
    {
        Assert.Equal("conversation.created", WebhookEventType.ConversationCreated.WireName());
        Assert.Equal("conversation.assigned", WebhookEventType.ConversationAssigned.WireName());
        Assert.Equal("conversation.closed", WebhookEventType.ConversationClosed.WireName());
        Assert.Equal("message.created", WebhookEventType.MessageCreated.WireName());
    }

    [Fact]
    public async Task HttpSender_PostsSignedPayloadVerbatim()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var sender = new HttpWebhookSender(new HttpClient(handler));
        var subscription = new WebhookSubscription
        {
            Url = "https://example.test/hooks",
            Secret = "whsec_abc",
        };
        var delivery = NewDelivery();

        var result = await sender.SendAsync(subscription, delivery, Now);

        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(delivery.Payload, handler.Body);
        Assert.Equal("application/json", handler.Request!.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(
            "conversation.created",
            handler.Request.Headers.GetValues(WebhookSigner.EventHeader).Single());
        Assert.Equal(
            delivery.Id.ToString(),
            handler.Request.Headers.GetValues(WebhookSigner.DeliveryHeader).Single());

        // The receiver can verify what we actually put on the wire.
        var signature = handler.Request.Headers.GetValues(WebhookSigner.SignatureHeader).Single();
        Assert.True(WebhookSigner.TryVerify(
            subscription.Secret, signature, handler.Body!, Now, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task HttpSender_TreatsNon2xxAsRejected()
    {
        var sender = new HttpWebhookSender(new HttpClient(new CapturingHandler(HttpStatusCode.InternalServerError)));

        var result = await sender.SendAsync(
            new WebhookSubscription { Url = "https://example.test/hooks", Secret = "s" },
            NewDelivery(), Now);

        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
    }

    [Fact]
    public async Task HttpSender_TreatsTransportFailureAsUnreachable()
    {
        var sender = new HttpWebhookSender(
            new HttpClient(new ThrowingHandler(new HttpRequestException("no route to host"))));

        var result = await sender.SendAsync(
            new WebhookSubscription { Url = "https://example.test/hooks", Secret = "s" },
            NewDelivery(), Now);

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("no route to host", result.Error);
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
