using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tundy;

/// <summary>A playlist/album link from a request.</summary>
/// <param name="Kind">"youtube", or Spotify's "playlist" / "album".</param>
public sealed record PlaylistLink(string Kind, string Id, string Url)
{
    public bool IsSpotify => Kind != "youtube";
}

/// <summary>One song from a playlist: a YouTube video, or a Spotify track still to be matched to YouTube.</summary>
public sealed record PlaylistSong(string Title, int? Seconds, string? YouTubeUrl, SpotifyTrack? Spotify);

/// <summary>The songs of a playlist that can be played, and how many were left out.</summary>
public sealed record PlaylistContents(string Name, IReadOnlyList<PlaylistSong> Songs, int Total, int SkippedLong, int SkippedLive);

/// <summary>Reads the songs of YouTube playlists and Spotify playlists/albums (no account or API key needed).</summary>
public static partial class PlaylistReader
{
    private static readonly HttpClient Http = CreateClient();

    [GeneratedRegex(@"<script id=""__NEXT_DATA__"" type=""application/json"">(?<json>.+?)</script>", RegexOptions.Singleline)]
    private static partial Regex NextData();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return c;
    }

    public static Task<PlaylistContents> ReadAsync(PlaylistLink link, YtDlp ytDlp, int maxDurationSeconds, CancellationToken ct) =>
        link.IsSpotify ? ReadSpotifyAsync(link, maxDurationSeconds, ct) : ReadYouTubeAsync(link, ytDlp, maxDurationSeconds, ct);

    /// <summary>Lists a YouTube playlist (up to 200 videos), leaving out live streams and videos that are too long.</summary>
    private static async Task<PlaylistContents> ReadYouTubeAsync(PlaylistLink link, YtDlp ytDlp, int maxDurationSeconds, CancellationToken ct)
    {
        var (name, entries) = await ytDlp.ListPlaylistAsync(link.Url, 200, ct);
        var songs = new List<PlaylistSong>();
        int tooLong = 0, live = 0;
        foreach (var e in entries)
        {
            if (e.Seconds == null) { live++; continue; }              // live streams / premieres have no length
            if (e.Seconds > maxDurationSeconds) { tooLong++; continue; }
            songs.Add(new PlaylistSong(TitleCleaner.Clean(e.Title), e.Seconds, $"https://www.youtube.com/watch?v={e.Id}", null));
        }
        return new PlaylistContents(name ?? "YouTube playlist", songs, entries.Count, tooLong, live);
    }

    /// <summary>
    /// Reads a Spotify playlist or album from Spotify's public embed page, which lists each song's title, artists
    /// and length. Songs longer than the limit are left out.
    /// </summary>
    private static async Task<PlaylistContents> ReadSpotifyAsync(PlaylistLink link, int maxDurationSeconds, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{link.Kind}/{link.Id}", ct);
        var m = NextData().Match(html);
        if (!m.Success) throw new InvalidOperationException("Couldn't read that Spotify playlist. Is it public?");

        using var doc = JsonDocument.Parse(m.Groups["json"].Value);
        if (!TryGet(doc.RootElement, out var entity, "props", "pageProps", "state", "data", "entity"))
            throw new InvalidOperationException("Couldn't read that Spotify playlist. Is it public?");

        var name = entity.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "Spotify playlist";
        var songs = new List<PlaylistSong>();
        int total = 0, tooLong = 0;
        if (entity.TryGetProperty("trackList", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in list.EnumerateArray())
            {
                total++;
                string S(string p) => t.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var uri = S("uri");
                if (!uri.StartsWith("spotify:track:", StringComparison.Ordinal)) continue; // podcasts etc.
                if (t.TryGetProperty("isPlayable", out var pl) && pl.ValueKind == JsonValueKind.False) continue;

                int? seconds = t.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (int)(d.GetInt64() / 1000) : null;
                if (seconds > maxDurationSeconds) { tooLong++; continue; }

                var artists = S("subtitle").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                           .Select(a => a.Replace(' ', ' ')).ToList();
                var track = new SpotifyTrack(uri["spotify:track:".Length..], S("title"), artists, link.Kind == "album" ? name : "", seconds, "");
                songs.Add(new PlaylistSong(track.Display, seconds, null, track));
            }
        }

        if (total == 0) throw new InvalidOperationException("That Spotify playlist looks empty or private.");
        return new PlaylistContents(name, songs, total, tooLong, 0);
    }

    private static bool TryGet(JsonElement e, out JsonElement result, params string[] path)
    {
        result = e;
        foreach (var p in path)
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(p, out result))
                return false;
        return true;
    }

    /// <summary>Shuffles (if asked) and keeps at most <paramref name="max"/> songs.</summary>
    public static List<PlaylistSong> Pick(IReadOnlyList<PlaylistSong> songs, int max, bool shuffle)
    {
        var list = songs.ToList();
        if (shuffle)
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        return list.Take(Math.Max(1, max)).ToList();
    }
}
