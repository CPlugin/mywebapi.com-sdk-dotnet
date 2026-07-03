using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>One page of a cursor-paginated list endpoint.</summary>
public sealed class Page<T>
{
    /// <summary>Items on this page.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>Opaque token for the next page; pass it back as the <c>cursor</c> argument.
    /// Null on the last page.</summary>
    public string? NextCursor { get; }

    /// <summary>True when more items are available (explicit boolean form of
    /// <see cref="NextCursor"/> != null).</summary>
    public bool HasMore { get; }

    /// <summary>W3C trace id of the response that produced this page.</summary>
    public string? ActivityId { get; }

    public Page(IReadOnlyList<T> items, string? nextCursor, bool hasMore, string? activityId)
    {
        Items = items;
        NextCursor = nextCursor;
        HasMore = hasMore;
        ActivityId = activityId;
    }
}

/// <summary>Cursor-walk helpers over any paged endpoint method.</summary>
/// <example>
/// <code>
/// var mt4 = client.MT4(tradePlatform);
/// await foreach (var user in PageIterator.ItemsAsync(cur => mt4.UsersRequestAsync(limit: 100, cursor: cur)))
///     Console.WriteLine(user.Login);
/// </code>
/// </example>
public static class PageIterator
{
    /// <summary>Yield pages until <see cref="Page{T}.HasMore"/> is false.
    /// <paramref name="fetch"/> receives the cursor (<c>null</c> for the first page).</summary>
    public static async IAsyncEnumerable<Page<T>> PagesAsync<T>(
        Func<string?, Task<Page<T>>> fetch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? cursor = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await fetch(cursor).ConfigureAwait(false);
            yield return page;
            if (!page.HasMore || page.NextCursor is null) yield break;
            cursor = page.NextCursor;
        }
    }

    /// <summary>Flattened item stream over <see cref="PagesAsync{T}"/>.</summary>
    public static async IAsyncEnumerable<T> ItemsAsync<T>(
        Func<string?, Task<Page<T>>> fetch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var page in PagesAsync(fetch, ct).ConfigureAwait(false))
        {
            foreach (var item in page.Items)
                yield return item;
        }
    }
}
