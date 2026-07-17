using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Domain;
using Harbor.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Infrastructure;

/// <summary>The JSON body delivered to subscribers.</summary>
public record WebhookPayload(
    Guid Id, string Event, Guid WorkspaceId, DateTimeOffset CreatedAt, object Data);

/// <summary>
/// Publishes domain events to a workspace's webhook subscriptions.
///
/// Publishing only queues rows; callers SaveChanges, so the delivery is
/// committed by the same transaction as the change that caused it. Nothing is
/// sent inline — an outbound HTTP call inside a request would tie the caller's
/// latency to the subscriber's and, worse, lose the event entirely if the
/// process died between the commit and the send.
/// </summary>
public static class Webhooks
{
    /// <summary>
    /// Payload serialization is pinned here rather than borrowed from MVC: the
    /// bytes are signed, so they must be reproducible independently of how the
    /// API happens to be configured to render responses.
    /// </summary>
    public static readonly JsonSerializerOptions PayloadJson =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() },
        };

    /// <summary>
    /// Queues a delivery for every active subscription in the workspace that
    /// wants this event. Returns the queued deliveries.
    /// </summary>
    public static async Task<List<WebhookDelivery>> PublishAsync(
        HarborDbContext db, Guid workspaceId, WebhookEventType eventType,
        object data, DateTimeOffset now)
    {
        var subscriptions = await db.WebhookSubscriptions
            .Where(s => s.WorkspaceId == workspaceId
                && s.IsActive
                && s.Events.Any(e => e.EventType == eventType))
            .ToListAsync();

        var deliveries = new List<WebhookDelivery>();
        foreach (var subscription in subscriptions)
        {
            var id = Guid.NewGuid();
            var payload = new WebhookPayload(
                id, eventType.WireName(), workspaceId, now, data);

            deliveries.Add(new WebhookDelivery
            {
                Id = id,
                SubscriptionId = subscription.Id,
                WorkspaceId = workspaceId,
                EventType = eventType,
                Payload = JsonSerializer.Serialize(payload, PayloadJson),
                CreatedAt = now,
                NextAttemptAt = now,
            });
        }

        db.WebhookDeliveries.AddRange(deliveries);
        return deliveries;
    }
}
