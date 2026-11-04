using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace CompanyManagement.Application.Common;

public class PagedRequest
{
    private int _pageSize = 20;
    public int Page { get; set; } = 1;
    public int PageSize { get => _pageSize; set => _pageSize = Math.Clamp(value, 1, 200); }
    public string? SortBy { get; set; }
    /// <summary>"asc" or "desc".</summary>
    public string? SortDir { get; set; }
    public string? Search { get; set; }

    public bool Descending => string.Equals(SortDir, "desc", StringComparison.OrdinalIgnoreCase);
}

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public static class QueryableExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PagedRequest req, CancellationToken ct = default)
    {
        var page = Math.Max(1, req.Page);
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page - 1) * req.PageSize).Take(req.PageSize).ToListAsync(ct);
        return new PagedResult<T>(items, total, page, req.PageSize);
    }

    public static PagedResult<T> ToPaged<T>(this IEnumerable<T> source, PagedRequest req)
    {
        var list = source as IList<T> ?? source.ToList();
        var page = Math.Max(1, req.Page);
        var items = list.Skip((page - 1) * req.PageSize).Take(req.PageSize).ToList();
        return new PagedResult<T>(items, list.Count, page, req.PageSize);
    }

    /// <summary>Applies a whitelisted sort. Unknown keys fall back to <paramref name="defaultKey"/>.</summary>
    public static IQueryable<T> ApplySort<T>(this IQueryable<T> query, PagedRequest req,
        IReadOnlyDictionary<string, Expression<Func<T, object?>>> sorts, string defaultKey, bool defaultDesc = false)
    {
        var key = req.SortBy is not null && sorts.ContainsKey(req.SortBy) ? req.SortBy : defaultKey;
        var desc = req.SortBy is not null && sorts.ContainsKey(req.SortBy) ? req.Descending : defaultDesc;
        var expr = sorts[key];
        return desc ? query.OrderByDescending(expr) : query.OrderBy(expr);
    }
}

public class NotFoundException(string entity, object key)
    : Exception($"{entity} '{key}' was not found.");

public class ForbiddenException(string message = "You do not have access to this resource.") : Exception(message);

public class ConflictException(string message) : Exception(message);

public class BadRequestException(string message) : Exception(message);
