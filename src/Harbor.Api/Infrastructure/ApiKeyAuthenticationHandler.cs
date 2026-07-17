using System.Security.Claims;
using System.Text.Encodings.Web;
using Harbor.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbor.Api.Infrastructure;

/// <summary>
/// Authenticates teammates by the <c>X-Api-Key</c> header. The key is hashed
/// and matched against <c>Teammate.ApiKeyHash</c>; the resulting principal
/// carries the teammate id, workspace id, and role.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    HarborDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string WorkspaceClaimType = "harbor:workspace";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values)
            || values.ToString() is not { Length: > 0 } apiKey)
        {
            return AuthenticateResult.NoResult();
        }

        var hash = ApiKeys.Hash(apiKey);
        var teammate = await db.Teammates
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.ApiKeyHash == hash);
        if (teammate is null)
        {
            return AuthenticateResult.Fail("Unknown API key.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, teammate.Id.ToString()),
                new Claim(ClaimTypes.Name, teammate.Name),
                new Claim(ClaimTypes.Role, teammate.Role.ToString()),
                new Claim(WorkspaceClaimType, teammate.WorkspaceId.ToString()),
            ],
            Scheme.Name);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
