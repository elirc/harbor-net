using Harbor.Domain;
using Harbor.Domain.Entities;

namespace Harbor.Api.Contracts;

public record WorkspaceResponse(Guid Id, string Name, DateTimeOffset CreatedAt);

public record InboxResponse(
    Guid Id, Guid WorkspaceId, string Name, int? FirstResponseSlaMinutes, DateTimeOffset CreatedAt);

public record ContactResponse(
    Guid Id, Guid WorkspaceId, string Name, string? Email, string? ExternalId,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt);

public record TeammateResponse(Guid Id, Guid WorkspaceId, string Name, string Email, DateTimeOffset CreatedAt);

public record TeamResponse(Guid Id, Guid WorkspaceId, string Name, IReadOnlyList<Guid> MemberIds, DateTimeOffset CreatedAt);

public record MessageResponse(
    Guid Id, Guid ConversationId, MessageKind Kind, AuthorType AuthorType,
    Guid? AuthorContactId, Guid? AuthorTeammateId, string Body, DateTimeOffset CreatedAt);

public record ConversationSummaryResponse(
    Guid Id, Guid WorkspaceId, Guid InboxId, Guid ContactId, string? Subject,
    ConversationState State, DateTimeOffset? SnoozedUntil, DateTimeOffset? ClosedAt,
    Guid? AssignedTeammateId, Guid? AssignedTeamId,
    DateTimeOffset? FirstResponseDueAt, DateTimeOffset? FirstRespondedAt,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset LastMessageAt);

public record ConversationDetailResponse(
    Guid Id, Guid WorkspaceId, Guid InboxId, Guid ContactId, string? Subject,
    ConversationState State, DateTimeOffset? SnoozedUntil, DateTimeOffset? ClosedAt,
    Guid? AssignedTeammateId, Guid? AssignedTeamId,
    DateTimeOffset? FirstResponseDueAt, DateTimeOffset? FirstRespondedAt,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset LastMessageAt,
    IReadOnlyList<MessageResponse> Messages);

public static class ResponseMappings
{
    public static WorkspaceResponse ToResponse(this Workspace w) => new(w.Id, w.Name, w.CreatedAt);

    public static InboxResponse ToResponse(this Inbox i) =>
        new(i.Id, i.WorkspaceId, i.Name, i.FirstResponseSlaMinutes, i.CreatedAt);

    public static ContactResponse ToResponse(this Contact c) =>
        new(c.Id, c.WorkspaceId, c.Name, c.Email, c.ExternalId, c.CreatedAt, c.LastSeenAt);

    public static TeammateResponse ToResponse(this Teammate t) =>
        new(t.Id, t.WorkspaceId, t.Name, t.Email, t.CreatedAt);

    public static TeamResponse ToResponse(this Team t) =>
        new(t.Id, t.WorkspaceId, t.Name, t.Members.Select(m => m.TeammateId).ToList(), t.CreatedAt);

    public static MessageResponse ToResponse(this Message m) =>
        new(m.Id, m.ConversationId, m.Kind, m.AuthorType, m.AuthorContactId, m.AuthorTeammateId, m.Body, m.CreatedAt);

    public static ConversationSummaryResponse ToSummaryResponse(this Conversation c) =>
        new(c.Id, c.WorkspaceId, c.InboxId, c.ContactId, c.Subject,
            c.State, c.SnoozedUntil, c.ClosedAt,
            c.AssignedTeammateId, c.AssignedTeamId,
            c.FirstResponseDueAt, c.FirstRespondedAt,
            c.Tags.Where(t => t.Tag is not null).Select(t => t.Tag!.Name).OrderBy(n => n).ToList(),
            c.CreatedAt, c.UpdatedAt, c.LastMessageAt);

    public static ConversationDetailResponse ToDetailResponse(this Conversation c) =>
        new(c.Id, c.WorkspaceId, c.InboxId, c.ContactId, c.Subject,
            c.State, c.SnoozedUntil, c.ClosedAt,
            c.AssignedTeammateId, c.AssignedTeamId,
            c.FirstResponseDueAt, c.FirstRespondedAt,
            c.Tags.Where(t => t.Tag is not null).Select(t => t.Tag!.Name).OrderBy(n => n).ToList(),
            c.CreatedAt, c.UpdatedAt, c.LastMessageAt,
            c.Messages.OrderBy(m => m.CreatedAt).Select(m => m.ToResponse()).ToList());
}
