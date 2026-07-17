using System.ComponentModel.DataAnnotations;

namespace Harbor.Api.Contracts;

public record CreateWorkspaceRequest(
    [Required, MaxLength(200)] string Name);

public record CreateInboxRequest(
    [Required, MaxLength(200)] string Name,
    [Range(1, 40_320)] int? FirstResponseSlaMinutes);

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
    [Required, EmailAddress, MaxLength(320)] string Email);

public record CreateTeamRequest(
    [Required, MaxLength(200)] string Name);

public record AddTeamMemberRequest(
    [Required] Guid TeammateId);

public record StartConversationRequest(
    [Required] Guid InboxId,
    [Required] Guid ContactId,
    [MaxLength(500)] string? Subject,
    [Required, MaxLength(20_000)] string Body);
