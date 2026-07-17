using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Infrastructure;

/// <summary>
/// Maps <see cref="DbUpdateConcurrencyException"/> to a 409 ProblemDetails.
///
/// 409 rather than 500: nothing is broken. Someone else changed the record
/// first, and the honest answer is to tell the caller to re-read and decide
/// again rather than silently overwrite the other person's work.
/// </summary>
public sealed class ConcurrencyExceptionHandler(IProblemDetailsService problemDetailsService)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Title = "Concurrent modification",
                Detail = "Someone else changed this record first. "
                    + "Re-read it and apply your change again.",
                Status = StatusCodes.Status409Conflict,
            },
        });
    }
}
