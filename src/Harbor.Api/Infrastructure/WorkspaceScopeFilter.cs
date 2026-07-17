using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Harbor.Api.Infrastructure;

/// <summary>
/// Denies any request whose <c>workspaceId</c> route value differs from the
/// authenticated teammate's workspace. Registered globally, so every
/// workspace-scoped route is tenant-isolated without per-action checks.
/// The comparison never touches the database, so a 403 reveals nothing about
/// whether the other workspace exists.
/// </summary>
public sealed class WorkspaceScopeFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!context.RouteData.Values.TryGetValue("workspaceId", out var raw)
            || raw is not string value
            || !Guid.TryParse(value, out var routeWorkspaceId))
        {
            return;
        }

        // Unauthenticated requests are rejected with 401 by the fallback policy.
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true || user.GetWorkspaceId() == routeWorkspaceId)
        {
            return;
        }

        context.Result = new ObjectResult(new ProblemDetails
        {
            Title = "Workspace access denied",
            Detail = "The API key does not grant access to this workspace.",
            Status = StatusCodes.Status403Forbidden,
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }
}
