using System;
using System.Collections.Generic;
using CPlugin.SaaSWebApi.Models;

namespace CPlugin.SaaSWebApi.Client;

/// <summary>Turns a v2 envelope into unwrapped data, throwing <see cref="ApiError"/>
/// when the envelope carries an error. Called from generated endpoint methods.</summary>
public static class EnvelopeGuard
{
    /// <summary>Return <paramref name="data"/>, or throw <see cref="ApiError"/> when
    /// <paramref name="error"/> is non-null.</summary>
    public static T Unwrap<T>(T data, Models.ApiError? error, ApiMeta? meta, int status)
    {
        ThrowIfError(error, meta, status);
        return data;
    }

    /// <summary>Wrap a cursor-paginated list envelope into <see cref="Page{T}"/>,
    /// or throw <see cref="ApiError"/> when <paramref name="error"/> is non-null.</summary>
    public static Page<T> UnwrapPage<T>(List<T>? data, Models.ApiError? error, ApiMeta? meta, int status)
    {
        ThrowIfError(error, meta, status);
        return new Page<T>(
            (IReadOnlyList<T>?)data ?? Array.Empty<T>(),
            meta?.Paging?.NextCursor,
            meta?.Paging?.HasMore ?? false,
            meta?.ActivityId);
    }

    private static void ThrowIfError(Models.ApiError? error, ApiMeta? meta, int status)
    {
        if (error is null) return;
        // * Enum member names mirror the wire values (NSwag keeps them verbatim),
        //   so ToString() round-trips the transport code exactly.
        throw new ApiError(
            error.Code?.ToString() ?? "Unknown",
            error.Message,
            meta?.ActivityId,
            error.ManagerCode?.ToString(),
            status);
    }
}
