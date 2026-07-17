namespace Harbor.Domain.Entities;

/// <summary>
/// A workspace's subscription to one or more events.
/// </summary>
public class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public required string Url { get; set; }

    /// <summary>
    /// The HMAC signing secret. Unlike a teammate's API key this is stored in
    /// the clear and cannot be hashed: signing every payload requires the
    /// original bytes. It is returned only when the subscription is created.
    /// </summary>
    public required string Secret { get; set; }

    /// <summary>Inactive subscriptions are skipped when events are published.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Workspace? Workspace { get; set; }
    public ICollection<WebhookSubscriptionEvent> Events { get; } = new List<WebhookSubscriptionEvent>();
}

/// <summary>Join row: one subscribed event type on a subscription.</summary>
public class WebhookSubscriptionEvent
{
    public Guid SubscriptionId { get; set; }
    public WebhookEventType EventType { get; set; }

    public WebhookSubscription? Subscription { get; set; }
}
