using System.ComponentModel.DataAnnotations;
using Harbor.Domain;

namespace Harbor.Api.Contracts;

/// <summary>
/// Bootstraps a workspace together with its first admin teammate. The
/// response carries the admin's API key — the only time it is revealed.
/// </summary>
public record CreateWorkspaceRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(200)] string AdminName,
    [Required, EmailAddress, MaxLength(320)] string AdminEmail);

public record CreateInboxRequest(
    [Required, MaxLength(200)] string Name,
    [Range(1, 40_320)] int? FirstResponseSlaMinutes,
    bool AutoAssign = false);

public record CreateContactRequest(
    [Required, MaxLength(200)] string Name,
    [EmailAddress, MaxLength(320)] string? Email,
    [MaxLength(200)] string? ExternalId);

public record UpdateContactRequest(
    [Required, MaxLength(200)] string Name,
    [EmailAddress, MaxLength(320)] string? Email,
    [MaxLength(200)] string? ExternalId);

public record CreateTeammateRequest(
    [Required, MaxLength(200)] string Name,
    [Required, EmailAddress, MaxLength(320)] string Email,
    TeammateRole Role = TeammateRole.Agent);

public record CreateTeamRequest(
    [Required, MaxLength(200)] string Name);

/// <summary>Sets a teammate's availability and open-conversation capacity.</summary>
public record UpdateAvailabilityRequest(
    TeammateAvailability Availability,
    [Range(1, 1_000)] int? CapacityLimit);

public record AddTeamMemberRequest(
    [Required] Guid TeammateId);

public record StartConversationRequest(
    [Required] Guid InboxId,
    [Required] Guid ContactId,
    [MaxLength(500)] string? Subject,
    [Required, MaxLength(20_000)] string Body,
    ConversationPriority? Priority = null);

public record AddMessageRequest(
    AuthorType AuthorType,
    [Required] Guid AuthorId,
    MessageKind Kind,
    [Required, MaxLength(20_000)] string Body);

public record ChangeStateRequest(
    ConversationState State,
    DateTimeOffset? SnoozedUntil);

/// <summary>
/// Assigns a conversation. Provide exactly one of TeammateId/TeamId,
/// or neither to unassign.
/// </summary>
public record AssignConversationRequest(Guid? TeammateId, Guid? TeamId);

public record SetPriorityRequest(ConversationPriority Priority);

/// <summary>
/// Creates an SLA policy. Null InboxId/Priority widen the policy to every
/// inbox/priority; at least one target must be set.
/// </summary>
public record CreateSlaPolicyRequest(
    [Required, MaxLength(200)] string Name,
    Guid? InboxId,
    ConversationPriority? Priority,
    [Range(1, 40_320)] int? FirstResponseMinutes,
    [Range(1, 40_320)] int? ResolutionMinutes);

public record UpdateSlaPolicyRequest(
    [Required, MaxLength(200)] string Name,
    Guid? InboxId,
    ConversationPriority? Priority,
    [Range(1, 40_320)] int? FirstResponseMinutes,
    [Range(1, 40_320)] int? ResolutionMinutes);

public record CreateTagRequest(
    [Required, MaxLength(100)] string Name);

public record CreateCannedReplyRequest(
    [Required, MaxLength(100)] string Shortcut,
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(20_000)] string Body);

public record UpdateCannedReplyRequest(
    [Required, MaxLength(100)] string Shortcut,
    [Required, MaxLength(200)] string Title,
    [Required, MaxLength(20_000)] string Body);

/// <summary>Bucket size for the conversation volume report.</summary>
public enum ReportInterval
{
    Hour = 0,
    Day = 1,
    Week = 2,
}

/// <summary>
/// Query-string filters shared by every report endpoint, so the same slice of
/// conversations can be viewed through any of them. From/To bound conversation
/// creation time and are inclusive/exclusive respectively.
/// </summary>
public record ReportFilterRequest
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public Guid? InboxId { get; init; }
    public Guid? AssignedTeammateId { get; init; }
    public Guid? AssignedTeamId { get; init; }
    public ConversationPriority? Priority { get; init; }

    /// <summary>Filter by tag name (exact, case-insensitive).</summary>
    public string? Tag { get; init; }
}

/// <summary>Query-string filters for the conversation list endpoint.</summary>
public record ConversationFilterRequest
{
    public ConversationState? State { get; init; }
    public Guid? InboxId { get; init; }
    public Guid? ContactId { get; init; }
    public Guid? AssignedTeammateId { get; init; }
    public Guid? AssignedTeamId { get; init; }

    /// <summary>When true, only conversations with no teammate/team assignment.</summary>
    public bool? Unassigned { get; init; }

    /// <summary>Filter by tag name (exact, case-insensitive).</summary>
    public string? Tag { get; init; }

    /// <summary>Case-insensitive search across subject and message bodies.</summary>
    public string? Q { get; init; }

    /// <summary>
    /// When true, only conversations that missed a first-response or
    /// resolution target.
    /// </summary>
    public bool? SlaBreached { get; init; }

    public ConversationPriority? Priority { get; init; }
}
