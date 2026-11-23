using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tundy;

/// <summary>The YouTube video picked for a Spotify track, and how sure the matcher is.</summary>
public sealed record SongMatch(string Url, string VideoTitle, int? VideoSeconds, double Score, string Query)
{
    public bool Confident => Score >= 45;
}

/// <summary>
/// Finds the YouTube upload that best matches a Spotify track. Instead of trusting the first search result, it
/// runs a couple of searches, then scores every result on:
/// - length: the strongest signal, since the same recording has the same length on Spotify and YouTube,
/// - how many of the song title's words and artist names appear in the video title,
/// - points off for other versions (live, cover, sped up, nightcore, karaoke, 1-hour loops...) unless the Spotify
///   title itself is that version, and a small bonus for "official audio".
/// </summary>
public static partial class SongMatcher
{
    // Words that mean "a different version" of a song. Only penalized when the Spotify track isn't that version.
    private static readonly string[] OtherVersionWords =
    {
        "live", "cover", "karaoke", "instrumental", "remix", "sped up", "speed up", "slowed", "reverb", "nightcore",
        "8d", "reaction", "hour", "hours", "loop", "extended", "bass boosted", "tutorial", "lesson", "acapella",
        "a cappella", "mashup", "piano version", "guitar cover", "lofi", "lo-fi", "concert", "full album", "rehearsal",
        "demo", "parody", "fan made", "fanmade", "ai cover", "edit audio",
    };

    // Tags Spotify adds that don't change the song; removed before searching.
    [GeneratedRegex(@"\s*[-–(\[]\s*(?:\d{4}\s+)?(?:digital(?:ly)?\s+)?remaster(?:ed)?(?:\s+\d{4})?(?:\s+version)?\s*[)\]]?|\s*[-–]\s*(?:single|radio|album)\s+(?:version|edit)\s*$|\s*\((?:single|radio|album)\s+(?:version|edit)\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTags();

    [GeneratedRegex(@"[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s+(?<who>[^\)\]]+)[\)\]]|\s+(?:feat\.?|ft\.?)\s+(?<who>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    /// <param name="log">Optional: receives a short report of the best candidates.</param>
    public static async Task<SongMatch> FindAsync(SpotifyTrack track, YtDlp ytDlp, int maxDurationSeconds, CancellationToken ct,
                                                  Action<string>? log = null)
    {
        var (baseTitle, featured) = CleanTitle(track.Title);
        var artists = track.Artists.Concat(featured).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mainArtist = artists.FirstOrDefault() ?? "";

        // Two searches at once: the cleaned title, and Spotify's exact title (YouTube's official uploads usually
        // use that, e.g. "Bohemian Rhapsody (Remastered 2011)"). A third one only if neither found a good match.
        var first = new List<string> { $"{string.Join(" ", artists.Take(2))} {baseTitle}".Trim() };
        var exact = $"{mainArtist} {track.Title}".Trim();
        if (!string.Equals(Normalize(exact), Normalize(first[0]), StringComparison.Ordinal)) first.Add(exact);
        var fallback = $"{mainArtist} {baseTitle} official audio".Trim();

        var seen = new HashSet<string>();
        var scored = new List<(YouTubeResult R, double Score, string Query)>();

        async Task Run(IEnumerable<string> queries)
        {
            var searches = queries.Select(async q =>
            {
                try { return (q, await ytDlp.SearchAsync(q, 8, ct)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log?.Invoke($"search \"{q}\" failed: {ex.Message}");
                    return (q, (IReadOnlyList<YouTubeResult>)Array.Empty<YouTubeResult>());
                }
            }).ToList();

            foreach (var (query, results) in await Task.WhenAll(searches))
                for (var i = 0; i < results.Count; i++)
                {
                    var r = results[i];
                    if (!seen.Add(r.Id)) continue;
                    if (r.Seconds is { } s && s > maxDurationSeconds) continue;
                    scored.Add((r, Score(track, baseTitle, artists, r, i), query));
                }
        }

        await Run(first);
        if (scored.Count == 0 || scored.Max(x => x.Score) < 75)
            await Run(new[] { fallback });

        if (scored.Count == 0)
            throw new InvalidOperationException("No matching YouTube video found.");

        var ranked = scored.OrderByDescending(x => x.Score).ToList();
        log?.Invoke($"\"{track.Display}\" ({Fmt(track.DurationSeconds)}): " + string.Join(" | ",
            ranked.Take(3).Select(x => $"{x.Score:F0} {Fmt(x.R.Seconds)} {x.R.Title}")));

        var best = ranked[0];
        return new SongMatch($"https://www.youtube.com/watch?v={best.R.Id}", best.R.Title, best.R.Seconds, best.Score, best.Query);
    }

    private static string Fmt(int? s) => s is { } v ? $"{v / 60}:{v % 60:00}" : "?:??";

    /// <summary>Removes remaster/radio-edit tags and pulls out "feat." artists.</summary>
    public static (string Title, List<string> Featured) CleanTitle(string title)
    {
        var featured = new List<string>();
        var t = Featuring().Replace(title, m =>
        {
            featured.AddRange(m.Groups["who"].Value.Split(new[] { ",", "&", " and ", " x " }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return " ";
        });
        t = NoiseTags().Replace(t, " ");
        t = Regex.Replace(t, @"\s+", " ").Trim(' ', '-', '–');
        return (t.Length > 0 ? t : title, featured);
    }

    private static double Score(SpotifyTrack track, string baseTitle, List<string> artists, YouTubeResult r, int rank)
    {
        var title = Normalize(r.Title);
        var channel = Normalize(r.Channel ?? "");
        var channelSquashed = channel.Replace(" ", "");
        var spotifyText = Normalize(track.Title + " " + track.Album);
        double score = 0;

        // 1. Length (worth the most). A few seconds either way is normal between uploads of the same recording.
        if (track.DurationSeconds is { } want && r.Seconds is { } got)
        {
            var diff = Math.Abs(got - want);
            score += diff switch { <= 3 => 40, <= 6 => 37, <= 10 => 28, <= 20 => 12, <= 45 => 0, _ => -30 };
            if (got > want * 1.8) score -= 40; // compilations and hour-long loops
        }
        else if (r.Seconds == null)
        {
            score -= 5; // live streams / unknown length
        }

        // 2. Title words.
        var titleWords = Words(baseTitle);
        if (titleWords.Count > 0)
            score += 30.0 * titleWords.Count(w => ContainsWord(title, w)) / titleWords.Count;

        // 3. Artists, in the video title or the channel name ("TheKillersMusic", "Queen Official", "Artist - Topic").
        bool ByArtist(string artist)
        {
            var a = Normalize(artist);
            return a.Length > 0 && (ContainsPhrase(title, a) || ContainsPhrase(channel, a) || channelSquashed.Contains(a.Replace(" ", "")));
        }
        if (artists.Count > 0)
        {
            if (ByArtist(artists[0])) score += 15;
            score += Math.Min(6, 3 * artists.Skip(1).Count(ByArtist));
        }

        // 4. Other versions, unless the Spotify track is that version.
        foreach (var word in OtherVersionWords)
            if (ContainsPhrase(title, word) && !ContainsPhrase(spotifyText, word))
                score -= 25;

        // 5. Official uploads of the recording, and titles that are exactly Spotify's (with or without the artist).
        var exactTitle = Normalize(track.Title);
        if (title == exactTitle || artists.Any(a => title == Normalize(a) + " " + exactTitle)) score += 6;
        if (r.IsOfficialAudio) score += 12;
        else if (ContainsPhrase(title, "official audio")) score += 8;
        else if (ContainsPhrase(title, "audio")) score += 5;

        // 6. Tie-breakers: popularity and YouTube's own ranking.
        if (r.Views is > 1000 and { } views) score += Math.Min(4, 0.6 * (Math.Log10(views) - 3));
        score += Math.Max(0, 8 - rank) * 0.3;

        return score;
    }

    private static List<string> Words(string s) =>
        Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1 || char.IsDigit(w[0])).Distinct().ToList();

    private static bool ContainsWord(string haystack, string word) => (" " + haystack + " ").Contains(" " + word + " ");

    private static bool ContainsPhrase(string haystack, string phrase) =>
        phrase.Length > 0 && (" " + haystack + " ").Contains(" " + phrase + " ");

    /// <summary>Lowercase, no accents, no punctuation, single spaces: "Beyoncé - Halo!" becomes "beyonce halo".</summary>
    public static string Normalize(string s)
    {
        var decomposed = s.ToLowerInvariant().Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
