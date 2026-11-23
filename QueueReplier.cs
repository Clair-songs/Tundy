using System;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;

namespace Tundy;

/// <summary>
/// Optionally tells the person who requested a song where it is in the queue.
///
/// Sends "/tell Name@World ..." to that exact person instead of "/r": /r goes to whoever sent you the most recent
/// tell, so with two people requesting songs close together it could reach the wrong one.
/// </summary>
public sealed partial class QueueReplier
{
    private readonly Configuration config;
    private readonly IFramework framework;
    private DateTime lastSent = DateTime.MinValue;

    public string LastResult { get; private set; } = "No replies sent yet.";

    public QueueReplier(Configuration config, IFramework framework)
    {
        this.config = config;
        this.framework = framework;
    }

    // Character names are "Firstname Lastname" with letters, apostrophes and hyphens; worlds are letters only.
    [GeneratedRegex(@"^[A-Za-z'\-]{2,15} [A-Za-z'\-]{2,15}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^[A-Za-z]{3,20}$")]
    private static partial Regex ValidWorld();

    /// <summary>Who to reply to, read from the tell's sender (name and home world).</summary>
    public static (string Name, string? World)? Target(byte[] encodedSender)
    {
        try
        {
            var sender = SeString.Parse(encodedSender);
            var player = sender.Payloads.OfType<PlayerPayload>().FirstOrDefault();
            var name = (player?.PlayerName ?? sender.TextValue).Trim();
            var start = 0;
            while (start < name.Length && !char.IsLetter(name[start])) start++; // drop link/icon characters
            name = name[start..];
            string? world = null;
            try { world = player?.World.ValueNullable?.Name.ExtractText(); } catch { /* no world data */ }
            return ValidName().IsMatch(name) ? (name, world is { } w && ValidWorld().IsMatch(w) ? w : null) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The message about a song's place in the queue.</summary>
    /// <param name="songsBefore">How many songs play before it (0 = it's starting now, 1 = up next).</param>
    public static string Message(string? song, int songsBefore)
    {
        var what = string.IsNullOrWhiteSpace(song) ? "Your song" : $"Your song \"{Announcer.Clean(song, 80)}\"";
        return songsBefore switch
        {
            <= 0 => $"{what} is starting now!",
            1 => $"{what} is up next!",
            _ => $"{what} is in the queue. {songsBefore} songs play before it.",
        };
    }

    /// <summary>The message for a playlist request.</summary>
    public static string PlaylistMessage(string playlist, int count, int songsBefore, bool shuffled = false) =>
        $"Added {count} song{(count == 1 ? "" : "s")} from \"{Announcer.Clean(playlist, 60)}\"{(shuffled ? ", shuffled" : "")}. "
        + (songsBefore <= 0 ? "The first one is starting now!" : songsBefore == 1 ? "The first one is up next!" : $"{songsBefore} songs play before the first one.");

    /// <summary>Sends <paramref name="message"/> by tell to the requester, if replies are on. Any thread.</summary>
    /// <param name="always">Send even with replies off (for answers people asked for, like "!q list").</param>
    public void Send(byte[] encodedSender, string requester, string message, bool always = false)
    {
        if ((!config.ReplyQueuePosition && !always) || requester == "You") return;
        if (Target(encodedSender) is not { } target)
        {
            LastResult = $"Couldn't reply to {requester}: couldn't read their name.";
            return;
        }

        var to = target.World != null ? $"{target.Name}@{target.World}" : target.Name;
        var command = $"/tell {to} {Announcer.Clean(message, 300)}";
        framework.RunOnFrameworkThread(() =>
        {
            // Never two tells within 2 seconds of each other (keeps the game's chat spam filter happy).
            var wait = TimeSpan.FromSeconds(2) - (DateTime.Now - lastSent);
            if (wait > TimeSpan.Zero)
            {
                framework.RunOnTick(() => SendNow(command), wait);
                lastSent = DateTime.Now + wait;
            }
            else
            {
                SendNow(command);
                lastSent = DateTime.Now;
            }
        });
    }

    private void SendNow(string command)
    {
        try
        {
            SongSharer.SendChatCommand(command);
            LastResult = $"Sent: {command}";
            Plugin.Log.Information($"Queue reply: {command}");
        }
        catch (Exception ex)
        {
            LastResult = $"Couldn't send the reply: {ex.Message}";
            Plugin.Log.Warning(ex, "Queue reply failed");
        }
    }
}
