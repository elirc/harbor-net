using Harbor.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Infrastructure;

/// <summary>
/// Drains the delivery outbox: sends every attempt that is due and records the
/// outcome, retrying with backoff until <see cref="WebhookDelivery.MaxAttempts"/>.
/// </summary>
public class WebhookDispatcher(HarborDbContext db, IWebhookSender sender)
{
    /// <summary>
    /// Attempts every pending delivery in the workspace whose NextAttemptAt has
    /// arrived. Returns the deliveries it touched, in attempt order.
    /// </summary>
    public async Task<List<WebhookDelivery>> DispatchAsync(
        Guid workspaceId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var due = await db.WebhookDeliveries
            .Include(d => d.Subscription)
            .Where(d => d.WorkspaceId == workspaceId
                && d.Status == WebhookDeliveryStatus.Pending
                && d.NextAttemptAt <= now)
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var delivery in due)
        {
            if (delivery.Subscription is not { } subscription)
            {
                // The subscription is gone; nothing to deliver to.
                delivery.Fail(null, "Subscription no longer exists.", now);
                continue;
            }

            if (!subscription.IsActive)
            {
                // Deactivating a subscription stops delivery without burning
                // attempts, so it can be paused and resumed.
                continue;
            }

            var result = await sender.SendAsync(subscription, delivery, now, cancellationToken);
            if (result.Success)
            {
                delivery.Succeed(result.StatusCode!.Value, now);
            }
            else
            {
                delivery.Fail(result.StatusCode, result.Error ?? "Delivery failed.", now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return due;
    }
}
