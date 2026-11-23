using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tundy;

/// <summary>What Spotify's public track page says about a song.</summary>
/// <param name="DurationSeconds">Song length from Spotify, used to pick the right YouTube upload.</param>
public sealed record SpotifyTrack(string Id, string Title, IReadOnlyList<string> Artists, string Album, int? DurationSeconds, string Year)
{
    public string ArtistText => string.Join(", ", Artists);
    public string Display => Artists.Count > 0 ? $"{ArtistText} - {Title}" : Title;
}

/// <summary>
/// Spotify audio is DRM-protected and cannot be downloaded, so we read the track's details from its public
/// page and play the matching song from YouTube instead (see <see cref="SongMatcher"/>).
/// </summary>
public static partial class SpotifyResolver
{
    private static readonly HttpClient Http = CreateClient();

    [GeneratedRegex(@"<meta (?:property|name)=""(?<k>[a-z:_]+)"" content=""(?<v>[^""]*)""")]
    private static partial Regex Meta();

    [GeneratedRegex(@"open\.spotify\.com/(?:intl-[\w-]+/)?(?<kind>track|playlist|album)/(?<id>[A-Za-z0-9]{22})")]
    private static partial Regex TrackUrl();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return c;
    }

    /// <summary>Reads title, all artists, album, length and year for a Spotify track ID.</summary>
    public static async Task<SpotifyTrack> GetTrackAsync(string trackId, CancellationToken ct)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/track/{trackId}", ct);

        var meta = new Dictionary<string, List<string>>();
        foreach (Match m in Meta().Matches(html))
        {
            var k = m.Groups["k"].Value;
            if (!meta.TryGetValue(k, out var list)) meta[k] = list = new List<string>();
            list.Add(WebUtility.HtmlDecode(m.Groups["v"].Value).Trim());
        }
        string? One(string k) => meta.TryGetValue(k, out var l) ? l.FirstOrDefault(v => v.Length > 0) : null;

        var title = One("og:title") ?? throw new InvalidOperationException("Could not read the Spotify track info.");

        // og:description looks like "Artist A, Artist B · Album · Song · 2025"
        var desc = (One("og:description") ?? "").Split('·', StringSplitOptions.TrimEntries);
        var artistText = One("music:musician_description") ?? (desc.Length > 0 ? desc[0] : "");
        var artists = artistText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var album = desc.Length > 1 ? desc[1] : "";
        var year = desc.Length > 3 ? desc[3] : (One("music:release_date") ?? "").Split('-')[0];
        int? duration = int.TryParse(One("music:duration"), out var d) && d > 0 ? d : null;

        return new SpotifyTrack(trackId, title, artists, album, duration, year);
    }

    /// <summary>Accepts a track ID or a spotify.link short link and returns the track ID.</summary>
    public static async Task<string> ResolveIdAsync(string idOrShortLink, CancellationToken ct)
    {
        if (!idOrShortLink.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return idOrShortLink;
        var (kind, id) = await ResolveShortLinkKindAsync(idOrShortLink, ct);
        return kind == "track" && id != null ? id : throw new InvalidOperationException("That Spotify link doesn't lead to a single song.");
    }

    /// <summary>What a spotify.link short link leads to: ("track" | "playlist" | "album", id), or (null, null).</summary>
    public static async Task<(string? Kind, string? Id)> ResolveShortLinkKindAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
        var m = TrackUrl().Match(resp.RequestMessage?.RequestUri?.ToString() ?? "");
        if (!m.Success) m = TrackUrl().Match(await resp.Content.ReadAsStringAsync(ct));
        return m.Success ? (m.Groups["kind"].Value, m.Groups["id"].Value) : (null, null);
    }

    /// <summary>Old API: "Artist - Title".</summary>
    public static async Task<string> ResolveAsync(string trackId, CancellationToken ct) => (await GetTrackAsync(trackId, ct)).Display;

    /// <summary>
    /// Turns a Spotify short link (spotify.link/..., from the phone app's Share button) into a track ID.
    /// Returns null if it doesn't lead to a single track.
    /// </summary>
    public static async Task<string?> ResolveShortLinkAsync(string url, CancellationToken ct)
    {
        var (kind, id) = await ResolveShortLinkKindAsync(url, ct);
        return kind == "track" ? id : null;
    }
}
