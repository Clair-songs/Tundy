using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tundy;

/// <summary>
/// Turns raw video titles into song names: "Billie Eilish - my future (Lyrics)" -> "Billie Eilish - my future".
/// Only removes extras that don't change which song it is (official video, lyrics, audio, HD/4K, remaster...);
/// keeps the parts that do (feat., remix, live, acoustic, "Gravagerz Version"...).
/// </summary>
public static partial class TitleCleaner
{
    // A bracketed part is dropped when it's only made of these words (plus years, numbers and punctuation).
    private static readonly string[] Noise =
    {
        "official", "oficial", "officiel", "ufficiale", "video", "vídeo", "vidéo", "music", "musical", "musica", "música",
        "audio", "áudio", "lyric", "lyrics", "letra", "letras", "paroles", "legendado", "legendada", "sub", "subs",
        "subtitulado", "subtitulada", "español", "espanol", "english", "eng", "translation", "traducción", "visualizer",
        "visualiser", "visual", "mv", "m/v", "pv", "hd", "hq", "4k", "8k", "uhd", "1080p", "720p", "60fps", "remaster",
        "remastered", "remasterizado", "version", "full", "clip", "videoclip", "clipe", "explicit", "clean", "color",
        "coded", "colour", "with", "and", "y", "e", "&", "the", "en", "con", "high", "quality", "sound", "stereo",
        "dirty", "uncensored", "censored", "premiere", "new", "nuevo", "estreno", "teaser", "short", "shorts", "vertical",
    };

    // Words that make a bracketed part matter (a different version of the song): never drop those.
    private static readonly string[] Keep =
    {
        "feat", "ft", "featuring", "remix", "rmx", "mix", "live", "en vivo", "ao vivo", "acoustic", "acústico", "acustico",
        "cover", "instrumental", "karaoke", "edit", "sped", "slowed", "nightcore", "reverb", "bootleg", "flip", "vip",
        "extended", "demo", "unplugged", "orchestral", "piano", "version)", "ver.",
    };

    [GeneratedRegex(@"\s*(\((?<in>[^()]*)\)|\[(?<in>[^\[\]]*)\]|【(?<in>[^【】]*)】|「(?<in>[^「」]*)」)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s*[|│｜]\s*(?<in>[^|│｜]*)$")]
    private static partial Regex PipeTail();

    [GeneratedRegex(@"[\p{L}\p{N}/&]+")]
    private static partial Regex Words();

    [GeneratedRegex(@"\s*[-–]?\s*\b(?:official\s+(?:music\s+)?(?:video|mv|m/v|audio|visualizer|lyric\s+video)|(?:video|audio)\s+oficial|lyric\s+video|lyrics?|m/?v|hd|4k)\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseTail();

    /// <summary>"Artist - Track" when YouTube knows both, otherwise the cleaned title.</summary>
    public static string Display(string title, string? artist, string? track)
    {
        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(track))
        {
            // YouTube sometimes lists every artist ("A, B, C"); the first few are enough.
            var artists = string.Join(", ", artist.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
            return $"{artists} - {track.Trim()}";
        }
        return Clean(title);
    }

    /// <summary>Removes "(Official Video)", "[Lyrics]", "| Official Music Video", "【MV】" and the like.</summary>
    public static string Clean(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var t = title.Trim();

        // Trailing "| Official Music Video" style parts, repeatedly.
        for (var i = 0; i < 3; i++)
        {
            var m = PipeTail().Match(t);
            if (!m.Success || !IsNoise(m.Groups["in"].Value)) break;
            t = t[..m.Index];
        }

        t = Brackets().Replace(t, m => IsNoise(m.Groups["in"].Value) ? "" : m.Value);

        // Unbracketed endings like "... Official Music Video" / "... Official MV" / "... Lyric Video".
        for (var i = 0; i < 2; i++)
        {
            var m = NoiseTail().Match(t);
            if (!m.Success || m.Index == 0) break;
            t = t[..m.Index];
        }

        t = Regex.Replace(t, @"\s{2,}", " ").Trim().TrimEnd('-', '–', '|', ':', ',').Trim();
        return t.Length > 0 ? t : title.Trim();
    }

    private static bool IsNoise(string part)
    {
        var p = part.ToLowerInvariant();
        if (Keep.Any(k => p.Contains(k))) return false;
        var words = Words().Matches(p).Select(m => m.Value).ToList();
        if (words.Count == 0) return true; // "()", "[]", "( )"
        return words.All(w => Noise.Contains(w) || w.All(char.IsDigit) || (w.Length == 4 && w.All(char.IsDigit)));
    }
}
