using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;

namespace Tundy;

/// <summary>
/// The jukebox link, inside Tundy: hides the link's tells from chat, answers another player's jukebox terminal
/// (their info, their songs, their queue), and passes answers on to your own terminal.
/// </summary>
public sealed partial class Plugin
{
    /// <summary>Your own jukebox terminal's link to another player's Tundy.</summary>
    public JukeboxClient Jukebox { get; private set; } = null!;

    private sealed class IncomingBatch
    {
        public required byte[] Encoded { get; init; }
        public required string?[] Parts { get; init; }
        public bool Shuffle { get; init; }
        public DateTime At { get; init; } = DateTime.Now;
    }

    private readonly Dictionary<string, IncomingBatch> jukeboxBatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> jukeboxRate = new(StringComparer.OrdinalIgnoreCase);

    public static string VersionText => Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    /// <summary>
    /// A tell that belongs to the jukebox link: hidden from chat (yours and, since they run Tundy too, theirs) and
    /// handled here. Returns true when it was one, so the normal request handling skips it.
    /// </summary>
    /// <param name="process">False: only hide it (it was already handled in <see cref="OnCheckMessageHandled"/>).</param>
    private bool HandleJukeboxTell(IHandleableChatMessage message, bool process = true)
    {
        var text = message.Message.TextValue;
        if (!JukeboxProtocol.IsProtocol(text)) return false;
        message.PreventOriginal();
        if (!process) return true;

        // For an outgoing tell the "sender" is who it went to.
        var encoded = message.Sender.Encode();
        var other = QueueReplier.Target(encoded);
        // The game still makes its chat bubble for this tell (over both of you), showing the previous message: hide it.
        bubbleGuard.LinkTell(other?.Name);
        if (message.LogKind != XivChatType.TellIncoming) return true; // our own outgoing link tells: just hide them
        if (other is not { } from) return true;
        var who = from.World != null ? $"{from.Name}@{from.World}" : from.Name;
        var body = text[JukeboxProtocol.Prefix.Length..].Trim();
        var space = body.IndexOf(' ');
        var verb = (space < 0 ? body : body[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : body[(space + 1)..].Trim();

        // Answers to our own terminal
        if (verb is "info" or "off" or "ack" or "qlist")
        {
            Jukebox.OnAnswer(from.Name, from.World, verb, rest);
            return true;
        }

        // Requests from someone's terminal: at most 6 link tells per person every 15 seconds.
        var now = DateTime.Now;
        if (!jukeboxRate.TryGetValue(from.Name, out var times)) jukeboxRate[from.Name] = times = new List<DateTime>();
        times.RemoveAll(t => (now - t).TotalSeconds > 15);
        if (times.Count >= 6) return true;
        times.Add(now);

        switch (verb)
        {
            case "hi":
                Replier.SendRawTo(who, Config.AllowJukebox ? JukeboxProtocol.Info(InfoFor(from.Name, from.World)) : JukeboxProtocol.Prefix + "off");
                if (Config.AllowJukebox) jukeboxListeners[who] = (from.Name, from.World, now);
                break;
            case "q":
                ReceiveJukeboxSongs(encoded, from.Name, who, rest);
                break;
            case "bye":
                // Their terminal closed: no more "now playing" updates for it.
                jukeboxListeners.Remove(who);
                break;
            case "getq" when Config.AllowJukebox:
                // The songs waiting, in play order (the one playing now is in "info").
                var ids = Player.FullQueue().Select(q => JukeboxProtocol.IdFor(q.Request.Url)).OfType<string>().Distinct().Take(120).ToList();
                foreach (var m in JukeboxProtocol.Chunk("qlist", ids)) Replier.SendRawTo(who, m);
                break;
        }
        return true;
    }

    /// <summary>
    /// Dalamud's first look at every chat message, before other plugins' chat handlers: the jukebox link's tells are
    /// handled and hidden here, so chat-bubble plugins and the like never see them. The game's own chat bubble for
    /// them is hidden by <see cref="BubbleGuard"/>.
    /// </summary>
    private void OnCheckMessageHandled(IHandleableChatMessage message)
    {
        if (message.LogKind is XivChatType.TellIncoming or XivChatType.TellOutgoing) HandleJukeboxTell(message);
    }

    // Terminals linked to you recently (said "hi" in the last 10 minutes): they get your info again when your song
    // changes, so their "now playing" stays current without asking every few seconds.
    private readonly Dictionary<string, (string Name, string? World, DateTime At)> jukeboxListeners = new(StringComparer.OrdinalIgnoreCase);
    private string? lastJukeboxSong;
    private DateTime lastJukeboxCheck = DateTime.MinValue;

    private void OnJukeboxTick(Dalamud.Plugin.Services.IFramework _)
    {
        if ((DateTime.Now - lastJukeboxCheck).TotalSeconds < 2) return;
        lastJukeboxCheck = DateTime.Now;
        var song = Player.Current?.Title ?? Player.Syncing?.Title;
        if (song == lastJukeboxSong) return;
        lastJukeboxSong = song;
        if (!Config.AllowJukebox) return;

        foreach (var (who, l) in jukeboxListeners.ToList())
        {
            if ((DateTime.Now - l.At).TotalMinutes > 10) { jukeboxListeners.Remove(who); continue; }
            Replier.SendRawTo(who, JukeboxProtocol.Info(InfoFor(l.Name, l.World)));
        }
    }

    private JukeboxInfo InfoFor(string sender, string? world)
    {
        var permission = PermissionFor(sender, world);
        var queue = Player.FullQueue();
        return new JukeboxInfo(VersionText,
            Accepting: Config.Enabled && Config.AllowJukebox && permission != RequestPermission.Blocked,
            NeedsApproval: permission == RequestPermission.PreQueue,
            CooldownSeconds: (int)(PlaylistCooldownLeft(sender)?.TotalSeconds ?? 0),
            MaxSongs: Config.MaxPlaylistSongs,
            Queued: Player.PendingCount,
            QueuedSeconds: queue.Sum(q => q.Request.KnownSeconds ?? 0),
            NowPlaying: Player.Current?.Title ?? Player.Syncing?.Title);
    }

    /// <summary>"q PART/PARTS SHUFFLE IDS": collects the parts of a send, then queues it under the usual rules.</summary>
    private void ReceiveJukeboxSongs(byte[] encoded, string sender, string who, string rest)
    {
        var p = rest.Split(' ', 3);
        if (p.Length < 2 || JukeboxProtocol.ParsePart(p[0]) is not { } part) return;
        var shuffle = p[1] == "1";
        var ids = p.Length > 2 ? p[2] : "";

        // Drop sends that were never finished.
        foreach (var stale in jukeboxBatches.Where(b => (DateTime.Now - b.Value.At).TotalSeconds > 60).Select(b => b.Key).ToList())
            jukeboxBatches.Remove(stale);

        if (part.Part == 1 || !jukeboxBatches.TryGetValue(sender, out var batch) || batch.Parts.Length != part.Parts)
        {
            batch = new IncomingBatch { Encoded = encoded, Parts = new string?[part.Parts], Shuffle = shuffle };
            jukeboxBatches[sender] = batch;
        }
        batch.Parts[part.Part - 1] = ids;
        if (batch.Parts.Any(x => x == null)) return;
        jukeboxBatches.Remove(sender);

        var all = JukeboxProtocol.ParseIds(string.Join(',', batch.Parts));
        var answer = QueueJukeboxSongs(encoded, sender, all, batch.Shuffle);
        Replier.SendRawTo(who, JukeboxProtocol.Prefix + "ack " + answer);
        // Fresh info right after (their terminal no longer asks for it with a "hi", which saves a tell each way).
        Replier.SendRawTo(who, JukeboxProtocol.Info(InfoFor(sender, QueueReplier.Target(encoded)?.World)));
    }

    /// <summary>Queues (or puts up for approval) songs sent from a jukebox terminal. Returns the answer for it.</summary>
    private string QueueJukeboxSongs(byte[] encoded, string sender, List<string> ids, bool shuffle)
    {
        string Refuse(string why)
        {
            AddLog(sender, "jukebox", $"Ignored jukebox songs: {why}", false);
            return "refused " + why;
        }

        var world = QueueReplier.Target(encoded)?.World;
        var permission = PermissionFor(sender, world);
        if (!Config.AllowJukebox) return Refuse("their jukebox is turned off");
        if (!Config.Enabled) return Refuse("they aren't taking requests right now");
        if (permission == RequestPermission.Blocked) return Refuse("you can't send them songs");
        if (ids.Count == 0) return Refuse("no songs came through");
        if (ids.Count > 1 && !Config.AllowPlaylists) return Refuse("they only take one song at a time");
        if (ids.Count > 1 && PlaylistCooldownLeft(sender) is { } wait) return Refuse($"you can send more in {Wait(wait)}");

        if (shuffle) ids = ids.OrderBy(_ => Random.Shared.Next()).ToList();
        var left = Math.Max(0, ids.Count - Config.MaxPlaylistSongs);
        ids = ids.Take(Config.MaxPlaylistSongs).ToList();
        if (ids.Count > 1) RememberPlaylist(sender, DateTime.UtcNow);

        if (permission == RequestPermission.PreQueue)
        {
            Pending.Add(new PendingRequest
            {
                EncodedSender = encoded,
                Sender = sender,
                Url = "jukebox",
                JukeboxIds = ids,
                Title = $"Jukebox: {ids.Count} song{(ids.Count == 1 ? "" : "s")}{(shuffle ? ", shuffled" : "")}",
            });
            AddLog(sender, "jukebox", $"{ids.Count} jukebox song(s) waiting for your approval", true);
            Chat.Print($"[Tundy] {sender} sent {ids.Count} song{(ids.Count == 1 ? "" : "s")} from their jukebox. Approve in /tundy → Now playing.");
            return $"waiting {ids.Count}" + (left > 0 ? $" ({left} over their limit left out)" : "");
        }

        EnqueueJukebox(encoded, sender, ids);
        return $"queued {ids.Count}" + (left > 0 ? $" ({left} over their limit left out)" : "");
    }

    /// <summary>Puts jukebox songs in the queue for the person who sent them.</summary>
    private void EnqueueJukebox(byte[] encoded, string sender, IReadOnlyList<string> ids)
    {
        var n = 0;
        foreach (var id in ids)
        {
            if (JukeboxProtocol.FromId(id) is not { } song) continue;
            if (Player.TryEnqueue(new SongRequest(song.Url, sender, song.SpotifyId) { EncodedSender = encoded, Playlist = "Jukebox" }, force: true)) n++;
        }
        AddLog(sender, "jukebox", $"Queued {n} song(s) from their jukebox", true);
        Notify($"Queued {n} song{(n == 1 ? "" : "s")} from {sender}'s jukebox.");
    }
}
