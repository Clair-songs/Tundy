using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tundy;

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
