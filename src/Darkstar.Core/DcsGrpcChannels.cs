using System.Collections.Concurrent;
using Grpc.Core;
using Grpc.Net.Client;

namespace Darkstar;

/// <summary>
/// Hands out gRPC channels to the DCS-gRPC server, one per address, reused for the life of the
/// process.
///
/// A <see cref="GrpcChannel"/> is not a request. It owns an HTTP/2 connection - socket, handshake
/// and all - and is explicitly designed to be created once and shared; every call on it is
/// multiplexed over that one connection. Creating one per request, which is what this code used
/// to do, paid for a fresh TCP connection on every bogey dope, every threat circle sweep (one per
/// circle every fifteen seconds) and every ATIS call, and left each one in TIME_WAIT afterwards.
///
/// The channel is lazy: constructing it connects to nothing, so a server that is down costs
/// nothing here and fails at the call, exactly as before.
/// </summary>
public static class DcsGrpcChannels
{
    private static readonly ConcurrentDictionary<string, GrpcChannel> Channels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The shared channel for an address. Never dispose the result - it belongs to this cache and
    /// is still in use elsewhere.
    /// </summary>
    public static GrpcChannel For(string address)
    {
        var key = (address ?? "").Trim();
        return Channels.GetOrAdd(key, a => GrpcChannel.ForAddress(a));
    }

    /// <summary>
    /// Builds the per-call metadata (just the API key, when one is configured). Cheap enough to
    /// do per call, and keeping it here means the header name is written once.
    /// </summary>
    public static Metadata HeadersFor(string? apiKey)
    {
        var headers = new Metadata();
        if (!string.IsNullOrWhiteSpace(apiKey))
            headers.Add("X-API-Key", apiKey);
        return headers;
    }

    /// <summary>
    /// Drops a cached channel, for the case where the configured address changed and the old
    /// connection is no longer wanted. Disposing it cancels anything still running on it, so this
    /// is for shutdown and reconfiguration, not for error handling.
    /// </summary>
    public static void Forget(string address)
    {
        if (Channels.TryRemove((address ?? "").Trim(), out var channel))
        {
            try { channel.Dispose(); } catch { /* best effort */ }
        }
    }

    /// <summary>Closes every channel. Called on shutdown.</summary>
    public static void DisposeAll()
    {
        foreach (var key in Channels.Keys.ToList())
            Forget(key);
    }
}
