using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tundy;

/// <summary>A video Tundy turned down itself (too long, a live stream...), with the reason in plain words.</summary>
public sealed class SongRejectedException : InvalidOperationException
{
    public string Reason { get; }

    public SongRejectedException(string reason) : base(reason) => Reason = reason;
}

/// <summary>Decides which download failures are worth retrying.</summary>
public static partial class RetryPolicy
{
    // Failures that won't go away by trying again: the video itself can't be played or was rejected on purpose.
    private static readonly string[] Permanent =
    {
        "video unavailable", "video is unavailable", "is not available", "does not exist", "private video", "this video is private", "has been removed", "removed by the uploader",
        "account associated with this video has been terminated", "copyright", "confirm your age", "age-restricted",
        "age restricted", "inappropriate for some users", "members-only", "join this channel", "premium",
        "video rejected", "does not pass filter", "live event", "is_live", "not available in your country",
        "blocked it in your country", "geo restricted", "no matching youtube video", "doesn't lead to",
        "playlists are turned off", "spotify links are turned off", "not a youtube or spotify",
    };

    /// <summary>True for network hiccups, YouTube throttling, bot checks, timeouts and other temporary errors.</summary>
    public static bool IsRetryable(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return true; // unknown failure: worth one more try
        var e = error.ToLowerInvariant();
        return !Permanent.Any(e.Contains);
    }

    /// <summary>Same as <see cref="IsRetryable(string?)"/>, and never for a video Tundy itself rejected.</summary>
    public static bool IsRetryable(Exception ex) => ex is not SongRejectedException && IsRetryable(ex.Message);

    // What YouTube (through yt-dlp) says, in lower case, for each case that can't be fixed by trying again.
    private static readonly (string[] Says, string Reason)[] Reasons =
    {
        (new[] { "private video", "this video is private" }, "it's a private video"),
        (new[] { "confirm your age", "age-restricted", "age restricted", "inappropriate for some users" }, "it's age-restricted (YouTube asks to sign in)"),
        (new[] { "not available in your country", "blocked it in your country", "geo restricted", "geo-restricted", "made this video available in your country" }, "it's not available in this region"),
        (new[] { "members-only", "members only", "join this channel" }, "it's for channel members only"),
        (new[] { "premieres in", "premiere will begin", "will begin in", "is_upcoming", "live event will begin" }, "it's a premiere or stream that hasn't started yet"),
        (new[] { "is_live", "live event", "this live stream" }, "it's a live stream"),
        (new[] { "no matching youtube video" }, "no matching song was found on YouTube"),
        (new[] { "has been removed", "removed by the uploader", "has been terminated", "copyright", "video unavailable", "video is unavailable", "no longer available", "does not exist", "is not available" }, "the video was removed or isn't available any more"),
    };

    /// <summary>
    /// Why a song couldn't be played, as one plain sentence that fits after "Couldn't play X's song: ", and whether
    /// trying again could help.
    /// </summary>
    public static (string Reason, bool CanRetry) Explain(Exception ex)
    {
        if (ex is SongRejectedException rejected) return (rejected.Reason, false);
        var e = (ex.Message ?? "").ToLowerInvariant();
        // Not a removed video: yt-dlp found no audio it can use, which a yt-dlp update usually fixes.
        if (e.Contains("requested format is not available"))
            return ("YouTube didn't offer a playable audio version (updating yt-dlp in Settings often fixes this)", true);
        foreach (var (says, reason) in Reasons)
            if (says.Any(e.Contains)) return (reason, false);
        if (IsBlockedByYouTube(ex.Message)) return ("YouTube refused the download (it often works again in a few minutes)", true);
        var shortText = Short(ex.Message);
        return (shortText, IsRetryable(ex.Message));
    }

    /// <summary>"3:45" or "1:02:03".</summary>
    public static string Length(int seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    /// <summary>YouTube refused the connection (HTTP 403 Forbidden or 503), which trying over IPv4 often fixes.</summary>
    public static bool IsBlockedByYouTube(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        var e = error.ToLowerInvariant();
        return e.Contains("http error 403") || e.Contains("http error 503") || e.Contains("403: forbidden")
               || e.Contains("503: service unavailable");
    }

    /// <summary>A short reason to show in chat (first line, trimmed of yt-dlp's "ERROR: [youtube] id:" prefix).</summary>
    public static string Short(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "unknown error";
        var line = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .LastOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) ?? error.Trim();
        line = Prefix().Replace(line, "");
        return line.Length > 120 ? line[..120] + "..." : line;
    }

    [GeneratedRegex(@"^(?:ERROR:\s*)?(?:\[[^\]]+\]\s*)?(?:[\w-]{11}:\s*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Prefix();

    [GeneratedRegex(@"(?:v=|youtu\.be/|shorts/|live/)(?<id>[\w-]{11})")]
    private static partial Regex VideoId();

    /// <summary>The 11-character YouTube video id in a link, or null.</summary>
    public static string? YouTubeId(string? url)
    {
        if (url == null) return null;
        var m = VideoId().Match(url);
        return m.Success ? m.Groups["id"].Value : null;
    }
}
