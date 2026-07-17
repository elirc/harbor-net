using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Tests.Integration;

/// <summary>
/// Stands in for the HTTP sender so dispatch can be driven without a live
/// endpoint. Tests set <see cref="Respond"/> to choose the outcome and read
/// <see cref="Sent"/> to see what would have gone out.
/// </summary>
public class FakeWebhookSender : IWebhookSender
{
    private readonly Lock _gate = new();
    private readonly List<SentWebhook> _sent = [];

    public record SentWebhook(string Url, string Secret, WebhookEventType EventType, string Payload);

    /// <summary>Decides each attempt's outcome; succeeds with 200 by default.</summary>
    public Func<WebhookDelivery, WebhookSendResult> Respond { get; set; } =
        _ => WebhookSendResult.Ok(200);

    public IReadOnlyList<SentWebhook> Sent
    {
        get
        {
            lock (_gate)
            {
                return _sent.ToList();
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _sent.Clear();
            Respond = _ => WebhookSendResult.Ok(200);
        }
    }

    public Task<WebhookSendResult> SendAsync(
        WebhookSubscription subscription, WebhookDelivery delivery,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _sent.Add(new SentWebhook(
                subscription.Url, subscription.Secret, delivery.EventType, delivery.Payload));
        }

        return Task.FromResult(Respond(delivery));
    }
}
