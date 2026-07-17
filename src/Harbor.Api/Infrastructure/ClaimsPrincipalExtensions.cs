using System.Security.Claims;
using Harbor.Domain;

namespace Harbor.Api.Infrastructure;

/// <summary>Typed accessors for the claims stamped by the API-key handler.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>Id of the authenticated teammate.</summary>
    public static Guid GetTeammateId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Workspace the authenticated teammate belongs to.</summary>
    public static Guid GetWorkspaceId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ApiKeyAuthenticationHandler.WorkspaceClaimType)!);

    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.IsInRole(nameof(TeammateRole.Admin));
}
