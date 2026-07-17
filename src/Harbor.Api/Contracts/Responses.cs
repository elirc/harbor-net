using Harbor.Domain;
using Harbor.Domain.Entities;
using Harbor.Infrastructure;

namespace Harbor.Api.Contracts;

public record WorkspaceResponse(Guid Id, string Name, DateTimeOffset CreatedAt);

public record InboxResponse(
    Guid Id, Guid WorkspaceId, string Name, int? FirstResponseSlaMinutes,
    bool AutoAssign, string? EmailAddress, DateTimeOffset CreatedAt);

/// <summary>An outbound reply rendered as an email, with its threading headers.</summary>
public record RenderedEmailResponse(
    Guid MessageId, string EmailMessageId, string From, string To, string Subject,
    string? InReplyTo, IReadOnlyList<string> References, string Body);

/// <summary>The result of ingesting an inbound email.</summary>
public record InboundEmailResponse(
    Guid ConversationId, Guid MessageId, Guid ContactId,
    bool StartedNewConversation, bool CreatedContact);

public record ContactResponse(
    Guid Id, Guid WorkspaceId, string Name, string? Email, string? ExternalId,
    IReadOnlyDictionary<string, string?> Attributes,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

public record SegmentResponse(
    Guid Id, Guid WorkspaceId, string Name, SegmentRuleSet Rules,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public record TeammateResponse(
    Guid Id, Guid WorkspaceId, string Name, string Email, TeammateRole Role,
    TeammateAvailability Availability, int? CapacityLimit, DateTimeOffset CreatedAt);

/// <summary>Returned only from teammate creation; the API key is never shown again.</summary>
public record TeammateCreatedResponse(
    Guid Id, Guid WorkspaceId, string Name, string Email, TeammateRole Role,
    DateTimeOffset CreatedAt, string ApiKey);

public record AssignmentEventResponse(
    Guid Id, Guid ConversationId, AssignmentKind Kind, Guid? ActorTeammateId,
    Guid? FromTeammateId, Guid? FromTeamId, Guid? ToTeammateId, Guid? ToTeamId,
    DateTimeOffset CreatedAt);

/// <summary>Returned from workspace bootstrap with the initial admin's API key.</summary>
public record CreateWorkspaceResponse(
    WorkspaceResponse Workspace, TeammateResponse Admin, string ApiKey);

public record TeamResponse(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<Guid> MemberIds, DateTimeOffset CreatedAt);

public record MessageResponse(
    Guid Id, Guid ConversationId, MessageKind Kind, AuthorType AuthorType,
    Guid? AuthorContactId, Guid? AuthorTeammateId, string Body,
    MessageChannel Channel, string? EmailMessageId, DateTimeOffset CreatedAt);

public record ConversationSummaryResponse(
    Guid Id, Guid WorkspaceId, Guid InboxId, Guid ContactId, string? Subject,
    ConversationState State, DateTimeOffset? SnoozedUntil, DateTimeOffset? ClosedAt,
    Guid? AssignedTeammateId, Guid? AssignedTeamId,
    ConversationPriority Priority, MessageChannel Channel,
    DateTimeOffset? FirstResponseDueAt, DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? ResolutionDueAt, DateTimeOffset? FirstResolvedAt, Guid? SlaPolicyId,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset LastMessageAt);

public record ConversationDetailResponse(
    Guid Id, Guid WorkspaceId, Guid InboxId, Guid ContactId, string? Subject,
    ConversationState State, DateTimeOffset? SnoozedUntil, DateTimeOffset? ClosedAt,
    Guid? AssignedTeammateId, Guid? AssignedTeamId,
    ConversationPriority Priority, MessageChannel Channel,
    DateTimeOffset? FirstResponseDueAt, DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? ResolutionDueAt, DateTimeOffset? FirstResolvedAt, Guid? SlaPolicyId,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset LastMessageAt,
    IReadOnlyList<MessageResponse> Messages);

/// <summary>A webhook subscription. The signing secret is deliberately absent.</summary>
public record WebhookResponse(
    Guid Id, Guid WorkspaceId, string Url, IReadOnlyList<WebhookEventType> Events,
    bool IsActive, DateTimeOffset CreatedAt);

/// <summary>Returned only from webhook creation; the secret is never shown again.</summary>
public record WebhookCreatedResponse(
    Guid Id, Guid WorkspaceId, string Url, IReadOnlyList<WebhookEventType> Events,
    bool IsActive, DateTimeOffset CreatedAt, string Secret);

public record WebhookDeliveryResponse(
    Guid Id, Guid SubscriptionId, WebhookEventType EventType, WebhookDeliveryStatus Status,
    int AttemptCount, DateTimeOffset? LastAttemptAt, DateTimeOffset NextAttemptAt,
    int? ResponseStatusCode, string? Error, DateTimeOffset? DeliveredAt,
    DateTimeOffset CreatedAt, string Payload);

/// <summary>
/// Distribution of a set of durations, in minutes. Null percentiles mean the
/// sample was empty — no conversation in the slice reached that milestone.
/// </summary>
public record DurationStatsResponse(
    int Count, double? P50Minutes, double? P90Minutes, double? P95Minutes, double? AverageMinutes);

/// <summary>Conversations started and closed within one time bucket.</summary>
public record VolumePointResponse(
    DateTimeOffset BucketStart, DateTimeOffset BucketEnd, int Started, int Closed);

public record VolumeReportResponse(
    ReportInterval Interval, int TotalStarted, int TotalClosed, IReadOnlyList<VolumePointResponse> Points);

/// <summary>Response/resolution timings plus SLA outcomes for a slice of conversations.</summary>
public record ResponseTimeReportResponse(
    int Conversations, int Awaiting, int Resolved,
    int FirstResponseBreaches, int ResolutionBreaches,
    DurationStatsResponse FirstResponse, DurationStatsResponse Resolution);

/// <summary>One row of a per-teammate or per-inbox breakdown.</summary>
public record BreakdownRowResponse(
    Guid? Id, string Name, int Conversations, int Open, int Snoozed, int Closed,
    int SlaBreached, DurationStatsResponse FirstResponse, DurationStatsResponse Resolution);

public record TagDistributionRowResponse(
    Guid TagId, string Name, int Conversations, double Share);

public record TagDistributionResponse(
    int Conversations, int Untagged, IReadOnlyList<TagDistributionRowResponse> Tags);

public record SlaPolicyResponse(
    Guid Id, Guid WorkspaceId, string Name, Guid? InboxId, ConversationPriority? Priority,
    int? FirstResponseMinutes, int? ResolutionMinutes, DateTimeOffset CreatedAt);

public record SlaBreachEventResponse(
    Guid Id, Guid ConversationId, SlaBreachKind Kind, DateTimeOffset DueAt,
    DateTimeOffset BreachedAt, Guid? SlaPolicyId, DateTimeOffset CreatedAt);

public record CollectionResponse(
    Guid Id, Guid WorkspaceId, string Name, string Slug, string? Description,
    int PublishedArticles, DateTimeOffset CreatedAt);

public record ArticleResponse(
    Guid Id, Guid WorkspaceId, Guid CollectionId, string Title, string Slug, string Body,
    ArticleStatus Status, DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A published article as the public help center sees it.</summary>
public record PublicArticleResponse(
    Guid Id, Guid CollectionId, string Title, string Slug, string Body,
    DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt);

/// <summary>A suggested article, with the score and the keywords that earned it.</summary>
public record SuggestedArticleResponse(
    Guid Id, Guid CollectionId, string Title, string Slug,
    int Score, IReadOnlyList<string> MatchedKeywords);

public record TagResponse(Guid Id, Guid WorkspaceId, string Name, DateTimeOffset CreatedAt);

public record CannedReplyResponse(
    Guid Id, Guid WorkspaceId, string Shortcut, string Title, string Body, DateTimeOffset CreatedAt);

public static class ResponseMappings
{
    public static TagResponse ToResponse(this Tag t) => new(t.Id, t.WorkspaceId, t.Name, t.CreatedAt);

    public static CannedReplyResponse ToResponse(this CannedReply r) =>
        new(r.Id, r.WorkspaceId, r.Shortcut, r.Title, r.Body, r.CreatedAt);

    public static WorkspaceResponse ToResponse(this Workspace w) => new(w.Id, w.Name, w.CreatedAt);

    public static CollectionResponse ToResponse(this ArticleCollection c, int publishedArticles) =>
        new(c.Id, c.WorkspaceId, c.Name, c.Slug, c.Description, publishedArticles, c.CreatedAt);

    public static ArticleResponse ToResponse(this Article a) =>
        new(a.Id, a.WorkspaceId, a.CollectionId, a.Title, a.Slug, a.Body,
            a.Status, a.PublishedAt, a.CreatedAt, a.UpdatedAt);

    public static PublicArticleResponse ToPublicResponse(this Article a) =>
        new(a.Id, a.CollectionId, a.Title, a.Slug, a.Body, a.PublishedAt, a.UpdatedAt);

    public static SuggestedArticleResponse ToResponse(this ArticleMatch m) =>
        new(m.Article.Id, m.Article.CollectionId, m.Article.Title, m.Article.Slug,
            m.Score, m.MatchedKeywords);

    public static InboxResponse ToResponse(this Inbox i) =>
        new(i.Id, i.WorkspaceId, i.Name, i.FirstResponseSlaMinutes, i.AutoAssign,
            i.EmailAddress, i.CreatedAt);

    public static RenderedEmailResponse ToResponse(this RenderedEmail e, Guid messageId) =>
        new(messageId, e.MessageId, e.From, e.To, e.Subject, e.InReplyTo, e.References, e.Body);

    public static ContactResponse ToResponse(this Contact c) =>
        new(c.Id, c.WorkspaceId, c.Name, c.Email, c.ExternalId,
            ContactAttributes.Read(c.AttributesJson), c.CreatedAt, c.LastSeenAt);

    public static SegmentResponse ToResponse(this Segment s, SegmentRuleSet rules) =>
        new(s.Id, s.WorkspaceId, s.Name, rules, s.CreatedAt, s.UpdatedAt);

    public static TeammateResponse ToResponse(this Teammate t) =>
        new(t.Id, t.WorkspaceId, t.Name, t.Email, t.Role, t.Availability, t.CapacityLimit, t.CreatedAt);

    public static AssignmentEventResponse ToResponse(this AssignmentEvent a) =>
        new(a.Id, a.ConversationId, a.Kind, a.ActorTeammateId,
            a.FromTeammateId, a.FromTeamId, a.ToTeammateId, a.ToTeamId, a.CreatedAt);

    public static TeammateCreatedResponse ToCreatedResponse(this Teammate t, string apiKey) =>
        new(t.Id, t.WorkspaceId, t.Name, t.Email, t.Role, t.CreatedAt, apiKey);

    public static TeamResponse ToResponse(this Team t) =>
        new(t.Id, t.WorkspaceId, t.Name, t.Members.Select(m => m.TeammateId).ToList(), t.CreatedAt);

    public static MessageResponse ToResponse(this Message m) =>
        new(m.Id, m.ConversationId, m.Kind, m.AuthorType, m.AuthorContactId, m.AuthorTeammateId,
            m.Body, m.Channel, m.EmailMessageId, m.CreatedAt);

    public static WebhookResponse ToResponse(this WebhookSubscription s) =>
        new(s.Id, s.WorkspaceId, s.Url,
            s.Events.Select(e => e.EventType).OrderBy(e => e).ToList(),
            s.IsActive, s.CreatedAt);

    public static WebhookCreatedResponse ToCreatedResponse(this WebhookSubscription s) =>
        new(s.Id, s.WorkspaceId, s.Url,
            s.Events.Select(e => e.EventType).OrderBy(e => e).ToList(),
            s.IsActive, s.CreatedAt, s.Secret);

    public static WebhookDeliveryResponse ToResponse(this WebhookDelivery d) =>
        new(d.Id, d.SubscriptionId, d.EventType, d.Status, d.AttemptCount,
            d.LastAttemptAt, d.NextAttemptAt, d.ResponseStatusCode, d.Error,
            d.DeliveredAt, d.CreatedAt, d.Payload);

    public static SlaPolicyResponse ToResponse(this SlaPolicy p) =>
        new(p.Id, p.WorkspaceId, p.Name, p.InboxId, p.Priority,
            p.FirstResponseMinutes, p.ResolutionMinutes, p.CreatedAt);

    public static SlaBreachEventResponse ToResponse(this SlaBreachEvent b) =>
        new(b.Id, b.ConversationId, b.Kind, b.DueAt, b.BreachedAt, b.SlaPolicyId, b.CreatedAt);

    public static ConversationSummaryResponse ToSummaryResponse(this Conversation c) =>
        new(c.Id, c.WorkspaceId, c.InboxId, c.ContactId, c.Subject,
            c.State, c.SnoozedUntil, c.ClosedAt,
            c.AssignedTeammateId, c.AssignedTeamId,
            c.Priority, c.Channel,
            c.FirstResponseDueAt, c.FirstRespondedAt,
            c.ResolutionDueAt, c.FirstResolvedAt, c.SlaPolicyId,
            c.Tags.Where(t => t.Tag is not null).Select(t => t.Tag!.Name).OrderBy(n => n).ToList(),
            c.CreatedAt, c.UpdatedAt, c.LastMessageAt);

    public static ConversationDetailResponse ToDetailResponse(this Conversation c) =>
        new(c.Id, c.WorkspaceId, c.InboxId, c.ContactId, c.Subject,
            c.State, c.SnoozedUntil, c.ClosedAt,
            c.AssignedTeammateId, c.AssignedTeamId,
            c.Priority, c.Channel,
            c.FirstResponseDueAt, c.FirstRespondedAt,
            c.ResolutionDueAt, c.FirstResolvedAt, c.SlaPolicyId,
            c.Tags.Where(t => t.Tag is not null).Select(t => t.Tag!.Name).OrderBy(n => n).ToList(),
            c.CreatedAt, c.UpdatedAt, c.LastMessageAt,
            c.Messages.OrderBy(m => m.CreatedAt).Select(m => m.ToResponse()).ToList());
}
