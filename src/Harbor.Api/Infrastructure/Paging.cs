using Microsoft.EntityFrameworkCore;

namespace Harbor.Api.Infrastructure;

/// <summary>
/// Page selection, bound from the query string on every list endpoint.
/// </summary>
public record PageRequest
{
    /// <summary>1-based; defaults to the first page.</summary>
    public int? Page { get; init; }

    /// <summary>Defaults to <see cref="Paging.DefaultPageSize"/>, capped at <see cref="Paging.MaxPageSize"/>.</summary>
    public int? PageSize { get; init; }
}

/// <summary>
/// Pagination for list endpoints.
///
/// The page travels in the query string and the totals come back in headers,
/// leaving response bodies as plain arrays. That keeps every existing client
/// and test working while removing the property that actually matters in
/// production: no list endpoint can return an unbounded number of rows any
/// more, because omitting the parameters selects a default page rather than
/// everything.
///
/// Out-of-range input is clamped rather than rejected: a page past the end is
/// an empty page, which is what a client walking pages expects to see.
/// </summary>
public static class Paging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public const string TotalCountHeader = "X-Total-Count";
    public const string PageHeader = "X-Page";
    public const string PageSizeHeader = "X-Page-Size";
    public const string TotalPagesHeader = "X-Total-Pages";

    public static int Page(this PageRequest request) => Math.Max(request.Page ?? 1, 1);

    public static int Size(this PageRequest request) =>
        Math.Clamp(request.PageSize ?? DefaultPageSize, 1, MaxPageSize);

    /// <summary>Counts the query, writes the paging headers, and returns one page.</summary>
    public static async Task<List<T>> ToPageAsync<T>(
        this IQueryable<T> query, PageRequest request, HttpResponse response,
        CancellationToken cancellationToken = default)
    {
        var total = await query.CountAsync(cancellationToken);
        var (page, size) = Describe(request, total, response);

        return await query
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The in-memory counterpart, for the few lists that must be ordered or
    /// projected in memory before they can be paged.
    /// </summary>
    public static List<T> ToPage<T>(
        this IReadOnlyList<T> items, PageRequest request, HttpResponse response)
    {
        var (page, size) = Describe(request, items.Count, response);

        return items
            .Skip((page - 1) * size)
            .Take(size)
            .ToList();
    }

    private static (int Page, int Size) Describe(
        PageRequest request, int total, HttpResponse response)
    {
        var page = request.Page();
        var size = request.Size();

        response.Headers[TotalCountHeader] = total.ToString();
        response.Headers[PageHeader] = page.ToString();
        response.Headers[PageSizeHeader] = size.ToString();
        response.Headers[TotalPagesHeader] =
            Math.Max(1, (int)Math.Ceiling((double)total / size)).ToString();

        return (page, size);
    }
}
