using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Tundy;

/// <summary>One song in a jukebox playlist saved on your PC.</summary>
public sealed class JukeboxSong
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public int? Seconds { get; set; }
}

/// <summary>A playlist you made in the jukebox terminal (saved in Tundy's settings).</summary>
public sealed class JukeboxPlaylist
{
    public string Name { get; set; } = "New playlist";
    public List<JukeboxSong> Songs { get; set; } = new();
}

/// <summary>What another Tundy said about itself when the terminal linked to it.</summary>
public sealed record JukeboxInfo(string Version, bool Accepting, bool NeedsApproval, int CooldownSeconds, int MaxSongs,
                                 int Queued, int QueuedSeconds, string? NowPlaying);

/// <summary>
/// The jukebox link between two Tundys, over tells (the only way plugins on two players' PCs can talk to each other).
/// Each message is one tell starting with <see cref="Prefix"/>; both Tundys hide those tells from chat. Songs travel
/// as YouTube video ids ("y" + 11 characters) or Spotify track ids ("s" + 22), many per tell, never as free links.
///
/// Messages (terminal → player with Tundy):
///   hi                         ask for their info
///   q PART/PARTS SHUFFLE IDS   songs to queue (ids comma-separated, split over several tells for big playlists)
///   getq                       ask for the ids of their queue
/// Answers:
///   info v=..|on=1|ap=0|cd=0|max=20|q=6|t=2280|np=Song title
///   off                        their jukebox is turned off
///   ack queued N | ack waiting N | ack refused REASON
///   qlist PART/PARTS IDS
/// </summary>
public static partial class JukeboxProtocol
{
    public const string Prefix = "~tdy1 ";

    /// <summary>Tells can be about 500 characters; leave room for "/tell Name@World ".</summary>
    private const int MaxPayload = 400;

    [GeneratedRegex(@"^(?:y[\w-]{11}|s[A-Za-z0-9]{22})$")]
    private static partial Regex IdRegex();

    public static bool IsProtocol(string text) => text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>A song link -> its compact id ("y..." or "s..."), or null for anything else.</summary>
    public static string? IdFor(string url)
    {
        if (RetryPolicy.YouTubeId(url) is { } y) return "y" + y;
        var m = Plugin.SpotifyRegex().Match(url.Trim());
        return m.Success ? "s" + m.Groups["id"].Value : null;
    }

    /// <summary>A compact id -> (link, Spotify id or null). Null if it isn't a valid id.</summary>
    public static (string Url, string? SpotifyId)? FromId(string id)
    {
        if (!IdRegex().IsMatch(id)) return null;
        return id[0] == 'y'
            ? ($"https://www.youtube.com/watch?v={id[1..]}", null)
            : ($"https://open.spotify.com/track/{id[1..]}", id[1..]);
    }

    /// <summary>Splits ids into the payloads of as few tells as possible ("q 1/2 0 id,id,...").</summary>
    public static List<string> Chunk(string verb, IReadOnlyList<string> ids, string extra = "")
    {
        var parts = new List<List<string>> { new() };
        var length = 0;
        foreach (var id in ids)
        {
            if (length + id.Length + 1 > MaxPayload - 30 && parts[^1].Count > 0)
            {
                parts.Add(new List<string>());
                length = 0;
            }
            parts[^1].Add(id);
            length += id.Length + 1;
        }
        var extraPart = extra.Length > 0 ? extra + " " : "";
        return parts.Select((p, i) => $"{Prefix}{verb} {i + 1}/{parts.Count} {extraPart}{string.Join(',', p)}".TrimEnd()).ToList();
    }

    /// <summary>"1/3" -> (1, 3); null if it isn't that.</summary>
    public static (int Part, int Parts)? ParsePart(string s)
    {
        var p = s.Split('/');
        return p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) && a >= 1 && b >= a && b <= 10
            ? (a, b)
            : null;
    }

    /// <summary>The valid ids in a comma-separated list (anything else is dropped).</summary>
    public static List<string> ParseIds(string s) =>
        s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
         .Where(id => IdRegex().IsMatch(id)).Distinct().Take(200).ToList();

    public static string Info(JukeboxInfo i) =>
        $"{Prefix}info v={i.Version}|on={(i.Accepting ? 1 : 0)}|ap={(i.NeedsApproval ? 1 : 0)}|cd={i.CooldownSeconds}|max={i.MaxSongs}"
        + $"|q={i.Queued}|t={i.QueuedSeconds}|np={Clip(i.NowPlaying ?? "", 120)}";

    public static JukeboxInfo? ParseInfo(string body)
    {
        var fields = new Dictionary<string, string>();
        var np = body.IndexOf("|np=", StringComparison.Ordinal);
        var head = np >= 0 ? body[..np] : body;
        foreach (var kv in head.Split('|'))
        {
            var eq = kv.IndexOf('=');
            if (eq > 0) fields[kv[..eq]] = kv[(eq + 1)..];
        }
        if (!fields.ContainsKey("v")) return null;
        int Int(string k) => fields.TryGetValue(k, out var v) && int.TryParse(v, out var n) ? n : 0;
        var nowPlaying = np >= 0 ? body[(np + 4)..] : "";
        return new JukeboxInfo(fields["v"], Int("on") == 1, Int("ap") == 1, Int("cd"), Int("max"), Int("q"), Int("t"),
                               nowPlaying.Length > 0 ? nowPlaying : null);
    }

    private static string Clip(string s, int max)
    {
        s = new string(s.Where(c => !char.IsControl(c) && c != '<' && c != '>').ToArray());
        return s.Length <= max ? s : s[..max];
    }
}
