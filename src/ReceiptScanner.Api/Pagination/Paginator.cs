using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using ReceiptScanner.Api.Errors;

namespace ReceiptScanner.Api.Pagination;

/// <summary>The envelope from API.md#pagination (DRF PageNumberPagination, config/api.py).</summary>
public sealed class PagedResponse<T>
{
    public int Count { get; init; }
    public int Page { get; init; }
    public int Pages { get; init; }
    public int PageSize { get; init; }
    public bool HasNext { get; init; }
    public bool HasPrevious { get; init; }
    public string? Next { get; init; }
    public string? Previous { get; init; }
    public required IReadOnlyList<T> Results { get; init; }
}

/// <summary>Which slice to fetch. Pure arithmetic, separated out so it can be tested without a database.</summary>
public readonly record struct PageWindow(int Number, int Size, int Count, int Pages)
{
    public const int DefaultSize = 20;
    public const int MaxSize = 100;

    public int Skip => (Number - 1) * Size;
    public bool HasNext => Number < Pages;
    public bool HasPrevious => Number > 1;

    /// <summary>
    /// DRF semantics: page_size that isn't a positive integer falls back to
    /// the default, and is capped at 100. A page that isn't an integer, is
    /// below 1 or past the end is a 404 — except page 1, which always exists.
    /// "last" means the last page.
    /// </summary>
    public static PageWindow Resolve(int count, string? pageParam, string? pageSizeParam)
    {
        var size = DefaultSize;
        if (int.TryParse(pageSizeParam, NumberStyles.None, CultureInfo.InvariantCulture, out var requested) && requested > 0)
            size = Math.Min(requested, MaxSize);

        var pages = Math.Max(1, (int)Math.Ceiling(count / (double)size));

        int number;
        if (string.IsNullOrEmpty(pageParam)) number = 1;
        else if (pageParam == "last") number = pages;
        else if (!int.TryParse(pageParam, NumberStyles.None, CultureInfo.InvariantCulture, out number))
            throw ApiException.NotFound("Invalid page.");

        if (number < 1 || number > pages) throw ApiException.NotFound("Invalid page.");
        return new PageWindow(number, size, count, pages);
    }
}

public static class Paginator
{
    /// <summary>
    /// Count, fetch one page, map it, and build the envelope with absolute
    /// next/previous links (honouring X-Forwarded-* via UseForwardedHeaders).
    /// The query must already be ordered.
    /// </summary>
    public static async Task<PagedResponse<TOut>> PaginateAsync<TIn, TOut>(
        IQueryable<TIn> query, Func<TIn, TOut> map, HttpRequest request, CancellationToken ct = default)
    {
        var count = await query.CountAsync(ct);
        var window = PageWindow.Resolve(count, request.Query["page"].FirstOrDefault(), request.Query["page_size"].FirstOrDefault());
        var rows = await query.Skip(window.Skip).Take(window.Size).ToListAsync(ct);
        return Build(window, rows.Select(map).ToList(), request);
    }

    /// <summary>The same envelope over a list already in memory (the reconcile queue is scored in C#).</summary>
    public static PagedResponse<TOut> PaginateList<TIn, TOut>(IReadOnlyList<TIn> items, Func<TIn, TOut> map, HttpRequest request)
    {
        var window = PageWindow.Resolve(items.Count, request.Query["page"].FirstOrDefault(), request.Query["page_size"].FirstOrDefault());
        return Build(window, items.Skip(window.Skip).Take(window.Size).Select(map).ToList(), request);
    }

    public static PagedResponse<T> Build<T>(PageWindow window, IReadOnlyList<T> results, HttpRequest request) => new()
    {
        Count = window.Count,
        Page = window.Number,
        Pages = window.Pages,
        PageSize = window.Size,
        HasNext = window.HasNext,
        HasPrevious = window.HasPrevious,
        Next = window.HasNext ? PageLink(request, window.Number + 1) : null,
        Previous = window.HasPrevious ? PageLink(request, window.Number - 1) : null,
        Results = results,
    };

    /// <summary>The current URL with ?page replaced; DRF drops the parameter entirely for page 1.</summary>
    internal static string PageLink(HttpRequest request, int page)
    {
        var query = QueryHelpers.ParseQuery(request.QueryString.Value)
            .Where(kv => kv.Key != "page")
            .SelectMany(kv => kv.Value.Select(v => new KeyValuePair<string, string?>(kv.Key, v)))
            .ToList();
        if (page > 1) query.Add(new("page", page.ToString(CultureInfo.InvariantCulture)));

        var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
        return query.Count == 0 ? baseUrl : QueryHelpers.AddQueryString(baseUrl, query);
    }
}
