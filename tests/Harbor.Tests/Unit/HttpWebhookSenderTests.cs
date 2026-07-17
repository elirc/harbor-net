using System.Net;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Tests.Unit;

/// <summary>
/// The real sender's wire format.
///
/// <see cref="WebhookSignerTests"/> proves the signing algorithm; these prove
/// the sender actually applies it to what goes out — the exact bytes, under the
/// subscription's secret, with the timestamp it signed. A receiver following
/// the documented recipe has to be able to verify it, so that is what is
/// asserted: sign here, verify there.
/// </summary>
public class HttpWebhookSenderTests
{
    private const string Secret = "whsec_0123456789abcdef";
    private const string Payload = """{"id":"1","event":"conversation.created","data":{"x":1}}""";
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private static WebhookSubscription Subscription() => new()
    {
        WorkspaceId = Guid.NewGuid(),
        Url = "https://example.test/hooks",
        Secret = Secret,
    };

    private static WebhookDelivery Delivery() => new()
    {
        SubscriptionId = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        EventType = WebhookEventType.ConversationCreated,
        Payload = Payload,
    };

    private static (HttpWebhookSender Sender, StubHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        return (new HttpWebhookSender(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    [Fact]
    public async Task Send_SignsTheExactBytes_SoAReceiverCanVerifyThem()
    {
        var (sender, handler) = Build(_ => Status(HttpStatusCode.OK));

        await sender.SendAsync(Subscription(), Delivery(), Now);

        // The receiver's side of the contract, run for real.
        Assert.True(WebhookSigner.TryVerify(
            Secret, handler.Signature!, handler.Body!, Now, Tolerance));
    }

    [Fact]
    public async Task Send_TransmitsThePayloadVerbatim()
    {
        var (sender, handler) = Build(_ => Status(HttpStatusCode.OK));

        await sender.SendAsync(Subscription(), Delivery(), Now);

        // Byte-for-byte: re-serializing anywhere in this path would break the
        // signature the receiver checks.
        Assert.Equal(Payload, handler.Body);
        Assert.Equal("application/json", handler.ContentType);
    }

    [Fact]
    public async Task Send_StampsTheTimestampItSigned()
    {
        var (sender, handler) = Build(_ => Status(HttpStatusCode.OK));

        await sender.SendAsync(Subscription(), Delivery(), Now);

        Assert.StartsWith($"t={Now.ToUnixTimeSeconds()},v1=", handler.Signature);
    }

    [Fact]
    public async Task Send_NamesTheEventAndDelivery_InHeaders()
    {
        var delivery = Delivery();
        var (sender, handler) = Build(_ => Status(HttpStatusCode.OK));

        await sender.SendAsync(Subscription(), delivery, Now);

        Assert.Equal("conversation.created", handler.Event);
        Assert.Equal(delivery.Id.ToString(), handler.DeliveryId);
    }

    [Fact]
    public async Task Send_PostsToTheSubscriptionUrl()
    {
        var (sender, handler) = Build(_ => Status(HttpStatusCode.OK));

        await sender.SendAsync(Subscription(), Delivery(), Now);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://example.test/hooks", handler.Url);
    }

    [Fact]
    public async Task ACapturedSignature_CannotBeReplayedLater()
    {
        var (sender, handler) = Build(_ => Status(HttpStatusCode.OK));
        await sender.SendAsync(Subscription(), Delivery(), Now);

        // An attacker who captured the request replays it ten minutes on. The
        // signature is still cryptographically intact — but it covers the
        // timestamp, which they cannot restamp without the secret.
        Assert.False(WebhookSigner.TryVerify(
            Secret, handler.Signature!, handler.Body!, Now.AddMinutes(10), Tolerance));
        Assert.True(WebhookSigner.TryVerify(
            Secret, handler.Signature!, handler.Body!, Now.AddMinutes(4), Tolerance));
    }

    [Fact]
    public async Task ASignatureFromAnotherSubscription_DoesNotVerify()
    {
        var (sender, handler) = Build(_ => Status(HttpStatusCode.OK));

        await sender.SendAsync(Subscription(), Delivery(), Now);

        Assert.False(WebhookSigner.TryVerify(
            "whsec_someoneelses", handler.Signature!, handler.Body!, Now, Tolerance));
    }

    // --- Outcome classification -------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.OK, 200)]
    [InlineData(HttpStatusCode.Created, 201)]
    [InlineData(HttpStatusCode.Accepted, 202)]
    [InlineData(HttpStatusCode.NoContent, 204)]
    public async Task Send_Any2xx_IsASuccess(HttpStatusCode code, int expected)
    {
        var (sender, _) = Build(_ => Status(code));

        var result = await sender.SendAsync(Subscription(), Delivery(), Now);

        Assert.True(result.Success);
        Assert.Equal(expected, result.StatusCode);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 400)]
    [InlineData(HttpStatusCode.Unauthorized, 401)]
    [InlineData(HttpStatusCode.NotFound, 404)]
    [InlineData(HttpStatusCode.TooManyRequests, 429)]
    [InlineData(HttpStatusCode.InternalServerError, 500)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
    public async Task Send_AnyNon2xx_IsRejected_AndKeepsTheStatus(HttpStatusCode code, int expected)
    {
        var (sender, _) = Build(_ => Status(code));

        var result = await sender.SendAsync(Subscription(), Delivery(), Now);

        // A subscriber's 4xx is still retried: it is more often a deploy in
        // progress than a permanent verdict, and the attempt budget bounds it.
        Assert.False(result.Success);
        Assert.Equal(expected, result.StatusCode);
        Assert.Contains(expected.ToString(), result.Error);
    }

    [Fact]
    public async Task Send_WhenTheHostIsUnreachable_IsUnreachable_WithNoStatus()
    {
        var (sender, _) = Build(_ => throw new HttpRequestException("connection refused"));

        var result = await sender.SendAsync(Subscription(), Delivery(), Now);

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("connection refused", result.Error);
    }

    [Fact]
    public async Task Send_WhenTheRequestTimesOut_IsUnreachable_NotAnUnhandledThrow()
    {
        var (sender, _) = Build(_ => throw new TaskCanceledException("The request timed out."));

        var result = await sender.SendAsync(Subscription(), Delivery(), Now);

        // A slow subscriber must not take the drain down with it.
        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.Contains("timed out", result.Error);
    }

    /// <summary>
    /// Captures what actually went on the wire. Header values are copied out
    /// eagerly because the request is disposed once the send returns.
    /// </summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public string? Signature { get; private set; }
        public string? Event { get; private set; }
        public string? DeliveryId { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public string? Url { get; private set; }
        public HttpMethod? Method { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Url = request.RequestUri?.ToString();
            Signature = Single(request, WebhookSigner.SignatureHeader);
            Event = Single(request, WebhookSigner.EventHeader);
            DeliveryId = Single(request, WebhookSigner.DeliveryHeader);
            if (request.Content is { } content)
            {
                Body = await content.ReadAsStringAsync(cancellationToken);
                ContentType = content.Headers.ContentType?.MediaType;
            }

            return respond(request);
        }

        private static string? Single(HttpRequestMessage request, string name) =>
            request.Headers.TryGetValues(name, out var values) ? values.Single() : null;
    }
}
