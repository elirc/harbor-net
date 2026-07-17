using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Api.Contracts;

public record WorkspaceResponse(Guid Id, string Name, DateTimeOffset CreatedAt);

public record InboxResponse(
    Guid Id, Guid WorkspaceId, string Name, int? FirstResponseSlaMinutes,
    bool AutoAssign, DateTimeOffset CreatedAt);

public record ContactResponse(
    Guid Id, Guid WorkspaceId, string Name, string? Email, string? ExternalId,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

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
    Guid? AuthorContactId, Guid? AuthorTeammateId, string Body, DateTimeOffset CreatedAt);

public record ConversationSummaryResponse(
    Guid Id, Guid WorkspaceId, Guid InboxId, Guid ContactId, string? Subject,
    ConversationState State, DateTimeOffset? SnoozedUntil, DateTimeOffset? ClosedAt,
    Guid? AssignedTeammateId, Guid? AssignedTeamId,
    ConversationPriority Priority,
    DateTimeOffset? FirstResponseDueAt, DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? ResolutionDueAt, DateTimeOffset? FirstResolvedAt, Guid? SlaPolicyId,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset LastMessageAt);

public record ConversationDetailResponse(
    Guid Id, Guid WorkspaceId, Guid InboxId, Guid ContactId, string? Subject,
    ConversationState State, DateTimeOffset? SnoozedUntil, DateTimeOffset? ClosedAt,
    Guid? AssignedTeammateId, Guid? AssignedTeamId,
    ConversationPriority Priority,
    DateTimeOffset? FirstResponseDueAt, DateTimeOffset? FirstRespondedAt,
    DateTimeOffset? ResolutionDueAt, DateTimeOffset? FirstResolvedAt, Guid? SlaPolicyId,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset LastMessageAt,
    IReadOnlyList<MessageResponse> Messages);

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

public record TagResponse(Guid Id, Guid WorkspaceId, string Name, DateTimeOffset CreatedAt);

public record CannedReplyResponse(
    Guid Id, Guid WorkspaceId, string Shortcut, string Title, string Body, DateTimeOffset CreatedAt);

public static class ResponseMappings
{
    public static TagResponse ToResponse(this Tag t) => new(t.Id, t.WorkspaceId, t.Name, t.CreatedAt);

    public static CannedReplyResponse ToResponse(this CannedReply r) =>
        new(r.Id, r.WorkspaceId, r.Shortcut, r.Title, r.Body, r.CreatedAt);

    public static WorkspaceResponse ToResponse(this Workspace w) => new(w.Id, w.Name, w.CreatedAt);

    public static InboxResponse ToResponse(this Inbox i) =>
        new(i.Id, i.WorkspaceId, i.Name, i.FirstResponseSlaMinutes, i.AutoAssign, i.CreatedAt);

    public static ContactResponse ToResponse(this Contact c) =>
        new(c.Id, c.WorkspaceId, c.Name, c.Email, c.ExternalId, c.CreatedAt, c.LastSeenAt);

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
        new(m.Id, m.ConversationId, m.Kind, m.AuthorType, m.AuthorContactId, m.AuthorTeammateId, m.Body, m.CreatedAt);

    public static SlaPolicyResponse ToResponse(this SlaPolicy p) =>
        new(p.Id, p.WorkspaceId, p.Name, p.InboxId, p.Priority,
            p.FirstResponseMinutes, p.ResolutionMinutes, p.CreatedAt);

    public static SlaBreachEventResponse ToResponse(this SlaBreachEvent b) =>
        new(b.Id, b.ConversationId, b.Kind, b.DueAt, b.BreachedAt, b.SlaPolicyId, b.CreatedAt);

    public static ConversationSummaryResponse ToSummaryResponse(this Conversation c) =>
        new(c.Id, c.WorkspaceId, c.InboxId, c.ContactId, c.Subject,
            c.State, c.SnoozedUntil, c.ClosedAt,
            c.AssignedTeammateId, c.AssignedTeamId,
            c.Priority,
            c.FirstResponseDueAt, c.FirstRespondedAt,
            c.ResolutionDueAt, c.FirstResolvedAt, c.SlaPolicyId,
            c.Tags.Where(t => t.Tag is not null).Select(t => t.Tag!.Name).OrderBy(n => n).ToList(),
            c.CreatedAt, c.UpdatedAt, c.LastMessageAt);

    public static ConversationDetailResponse ToDetailResponse(this Conversation c) =>
        new(c.Id, c.WorkspaceId, c.InboxId, c.ContactId, c.Subject,
            c.State, c.SnoozedUntil, c.ClosedAt,
            c.AssignedTeammateId, c.AssignedTeamId,
            c.Priority,
            c.FirstResponseDueAt, c.FirstRespondedAt,
            c.ResolutionDueAt, c.FirstResolvedAt, c.SlaPolicyId,
            c.Tags.Where(t => t.Tag is not null).Select(t => t.Tag!.Name).OrderBy(n => n).ToList(),
            c.CreatedAt, c.UpdatedAt, c.LastMessageAt,
            c.Messages.OrderBy(m => m.CreatedAt).Select(m => m.ToResponse()).ToList());
}
