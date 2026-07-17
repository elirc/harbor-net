namespace Harbor.Domain.Entities;

public enum WebhookDeliveryStatus
{
    /// <summary>Queued, or waiting for its next retry.</summary>
    Pending = 0,

    /// <summary>The endpoint answered 2xx.</summary>
    Succeeded = 1,

    /// <summary>Retries are exhausted; this delivery is dead.</summary>
    Failed = 2,
}

/// <summary>
/// One webhook payload owed to one subscription.
///
/// Deliveries are written in the same SaveChanges as the state change that
/// caused them — a transactional outbox. That is what makes the event log
/// match reality: an event cannot be published for a change that rolled back,
/// and a change cannot commit while silently dropping its event.
/// </summary>
public class WebhookDelivery : IHasVersion
{
    /// <summary>
    /// Optimistic-concurrency token. Two dispatchers draining the outbox at
    /// once must not both send the same payload; the loser's SaveChanges fails
    /// rather than double-delivering.
    /// </summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    /// <summary>Attempts allowed before the delivery is abandoned.</summary>
    public const int MaxAttempts = 5;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public Guid WorkspaceId { get; set; }
    public WebhookEventType EventType { get; set; }

    /// <summary>The exact JSON body that is signed and sent, byte for byte.</summary>
    public required string Payload { get; set; }

    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>When this delivery becomes eligible; set to now on creation.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    public int? ResponseStatusCode { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public WebhookSubscription? Subscription { get; set; }

    /// <summary>Records a successful attempt.</summary>
    public void Succeed(int statusCode, DateTimeOffset now)
    {
        AttemptCount++;
        LastAttemptAt = now;
        DeliveredAt = now;
        ResponseStatusCode = statusCode;
        Error = null;
        Status = WebhookDeliveryStatus.Succeeded;
    }

    /// <summary>
    /// Records a failed attempt and schedules the next one with exponential
    /// backoff (1, 2, 4, 8 minutes), or gives up once attempts are exhausted.
    /// </summary>
    public void Fail(int? statusCode, string error, DateTimeOffset now)
    {
        AttemptCount++;
        LastAttemptAt = now;
        ResponseStatusCode = statusCode;
        Error = error;

        if (AttemptCount >= MaxAttempts)
        {
            Status = WebhookDeliveryStatus.Failed;
            return;
        }

        Status = WebhookDeliveryStatus.Pending;
        NextAttemptAt = now.AddMinutes(Math.Pow(2, AttemptCount - 1));
    }
}
