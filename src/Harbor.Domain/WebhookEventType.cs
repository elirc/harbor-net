using System.Text.Json.Serialization;

namespace Harbor.Domain;

/// <summary>
/// Events a workspace can subscribe to. The wire names are dotted and stable;
/// the enum member names are not part of the contract.
/// </summary>
public enum WebhookEventType
{
    [JsonStringEnumMemberName("conversation.created")]
    ConversationCreated = 0,

    [JsonStringEnumMemberName("conversation.assigned")]
    ConversationAssigned = 1,

    [JsonStringEnumMemberName("conversation.closed")]
    ConversationClosed = 2,

    [JsonStringEnumMemberName("message.created")]
    MessageCreated = 3,
}

public static class WebhookEventTypes
{
    private static readonly Dictionary<WebhookEventType, string> WireNames = new()
    {
        [WebhookEventType.ConversationCreated] = "conversation.created",
        [WebhookEventType.ConversationAssigned] = "conversation.assigned",
        [WebhookEventType.ConversationClosed] = "conversation.closed",
        [WebhookEventType.MessageCreated] = "message.created",
    };

    /// <summary>The dotted name carried in the payload and the X-Harbor-Event header.</summary>
    public static string WireName(this WebhookEventType type) =>
        WireNames.TryGetValue(type, out var name) ? name : type.ToString();
}
