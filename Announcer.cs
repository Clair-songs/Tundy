using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;

namespace Tundy;

/// <summary>
/// Optionally posts "Now playing: <song> - requested by <name>" in a chat channel (e.g. /yell) when a song starts.
/// Song titles come from other people, so the text is cleaned before it's sent: no line breaks, no &lt;...&gt;
/// (the game would turn &lt;pos&gt; or &lt;t&gt; into your position or target), and it's kept short.
/// </summary>
public sealed partial class Announcer
{
    /// <summary>Chat channels you can announce in.</summary>
    public static readonly (string Command, string Name)[] Channels =
    {
        ("/yell", "Yell"), ("/say", "Say"), ("/shout", "Shout"), ("/party", "Party"), ("/fc", "Free Company"),
    };

    public const string DefaultTemplate = "Now playing: {title} - requested by {requester}";
    private const int MaxMessageBytes = 400; // the game allows 500; leave room
    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(20);

    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private DateTime lastSent = DateTime.MinValue;
    private string? lastSong;

    public string LastResult { get; private set; } = "Nothing announced yet.";

    public Announcer(Configuration config, IFramework framework, IObjectTable objects)
    {
        this.config = config;
        this.framework = framework;
        this.objects = objects;
    }

    /// <summary>Announces a song that just started, if announcements are on. Safe to call from any thread.</summary>
    public void SongStarted(string title, string? requester)
    {
        if (!config.AnnounceSongs) return;
        framework.RunOnFrameworkThread(() =>
        {
            if (title == lastSong && DateTime.Now - lastSent < TimeSpan.FromMinutes(1)) return; // same song again (e.g. replayed)
            if (DateTime.Now - lastSent < MinGap)
            {
                LastResult = $"Skipped \"{title}\": less than {MinGap.TotalSeconds:F0}s since the last announcement.";
                return;
            }

            lastSent = DateTime.Now;
            lastSong = title;
            Send(Build(config.AnnounceTemplate, title, requester));
        });
    }

    /// <summary>Sends an example announcement right away (from the Settings tab). Call on the framework thread.</summary>
    public void SendTest() => Send(Build(config.AnnounceTemplate, "Example Song", "Someone"));

    /// <summary>Sends the message in the chosen channel. Must run on the framework thread.</summary>
    private void Send(string text)
    {
        var channel = Channels.Any(c => c.Command == config.AnnounceChannel) ? config.AnnounceChannel : "/yell";
        var command = $"{channel} {text}";
        try
        {
            SongSharer.SendChatCommand(command);
            LastResult = $"Sent: {command}";
            Plugin.Log.Information($"Announced: {command}");
        }
        catch (Exception ex)
        {
            LastResult = $"Couldn't send the announcement: {ex.Message}";
            Plugin.Log.Warning(ex, "Announcement failed");
        }
    }

    /// <summary>Fills in the template and cleans it so it's safe to send as one chat message.</summary>
    public string Build(string template, string title, string? requester)
    {
        var who = requester;
        if (string.IsNullOrWhiteSpace(who) || who == "You") who = objects.LocalPlayer?.Name.TextValue ?? "me";

        var t = string.IsNullOrWhiteSpace(template) ? DefaultTemplate : template;
        var text = t.Replace("{title}", Clean(title, 120), StringComparison.OrdinalIgnoreCase)
                    .Replace("{requester}", Clean(who, 40), StringComparison.OrdinalIgnoreCase);
        text = Clean(text, int.MaxValue);
        if (text.Length == 0) text = Clean($"Now playing: {title}", int.MaxValue);

        // Stay under the game's message limit (it counts bytes).
        while (Encoding.UTF8.GetByteCount(text) > MaxMessageBytes) text = text[..^1];
        return text.TrimEnd();
    }

    /// <summary>One line, no control characters, no &lt; &gt; (game text placeholders), single spaces.</summary>
    public static string Clean(string s, int maxLength)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsControl(c) || c is '<' or '>' || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.PrivateUse
                    or System.Globalization.UnicodeCategory.Format)
                sb.Append(' ');
            else
                sb.Append(c);
        }
        var clean = Spaces().Replace(sb.ToString(), " ").Trim();
        return clean.Length > maxLength ? clean[..maxLength].TrimEnd() + "..." : clean;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
