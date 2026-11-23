using System;
using System.Collections.Generic;
using System.Linq;

namespace Tundy;

/// <summary>A song request waiting for your approval (see <see cref="AccessMode.EveryonePreQueue"/>).</summary>
public sealed class PendingRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public required byte[] EncodedSender { get; init; }
    public required string Sender { get; init; }
    public required string Url { get; init; }
    public PlaylistLink? Playlist { get; init; }
    public string? SpotifyId { get; init; }

    /// <summary>Sent with a shuffle command: the playlist is shuffled when it's approved.</summary>
    public bool Shuffle { get; init; }
    public DateTime RequestedAt { get; } = DateTime.Now;

    /// <summary>Song or playlist name, looked up in the background. Null until it's known.</summary>
    public string? Title { get; set; }

    /// <summary>The song's length in seconds, once it's looked up.</summary>
    public int? Seconds { get; set; }

    public string Display => Title ?? (Playlist != null ? $"Playlist: {Url}" : Url);
}

/// <summary>Requests waiting for approval, oldest first. Thread-safe.</summary>
public sealed class PreQueue
{
    private readonly List<PendingRequest> items = new();

    public PendingRequest[] Snapshot()
    {
        lock (items) return items.ToArray();
    }

    public int Count
    {
        get { lock (items) return items.Count; }
    }

    public void Add(PendingRequest r)
    {
        lock (items) items.Add(r);
    }

    /// <summary>Takes a request out (to approve or reject it). Null if it's already gone.</summary>
    public PendingRequest? Take(Guid id)
    {
        lock (items)
        {
            var r = items.FirstOrDefault(x => x.Id == id);
            if (r != null) items.Remove(r);
            return r;
        }
    }

    public PendingRequest[] TakeAll()
    {
        lock (items)
        {
            var all = items.ToArray();
            items.Clear();
            return all;
        }
    }
}
