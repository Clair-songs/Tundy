using System;
using System.Collections.Generic;
using System.Linq;

namespace Tundy;

/// <summary>Your jukebox terminal's link to one other player's Tundy (see JukeboxProtocol).</summary>
public sealed class JukeboxClient
{
    public enum LinkState { Idle, Connecting, Linked, Off, Failed }

    private readonly Plugin plugin;

    public JukeboxClient(Plugin plugin) => this.plugin = plugin;

    public string? TargetName { get; private set; }
    public string? TargetWorld { get; private set; }
    public LinkState State { get; private set; } = LinkState.Idle;
    public JukeboxInfo? Info { get; private set; }
    public DateTime InfoAt { get; private set; }
    public TimeSpan? Ping { get; private set; }

    /// <summary>The answer to the last send, in plain words (shown under the buttons).</summary>
    public string LastResult { get; private set; } = "";
    public bool LastResultOk { get; private set; } = true;

    /// <summary>Raised with the song ids of their queue after "Save their queue".</summary>
    public event Action<string, List<string>>? QueueReceived;

    private DateTime sentAt;
    private bool waitingForAck;
    private string?[]? qlistParts;

    private string Who => TargetWorld != null ? $"{TargetName}@{TargetWorld}" : TargetName!;

    /// <summary>Links to a player: asks their Tundy for its info. No answer within 10 s means they don't have it.</summary>
    public void Connect(string name, string? world)
    {
        TargetName = name;
        TargetWorld = world;
        Info = null;
        TheirQueue = Array.Empty<string>();
        TheirQueueAt = DateTime.MinValue;
        QueueRequested = false;
        Ping = null;
        LastResult = "";
        State = LinkState.Connecting;
        Hello();
    }

    public void Disconnect()
    {
        Bye();
        State = LinkState.Idle;
        TargetName = TargetWorld = null;
        Info = null;
    }

    // The terminal was closed while linked: their Tundy was told to stop sending updates.
    private bool asleep;

    /// <summary>The terminal closed: tells their Tundy to stop sending "now playing" updates. The link is kept.</summary>
    public void Sleep()
    {
        if (asleep || TargetName == null || State != LinkState.Linked) return;
        Bye();
        asleep = true;
    }

    private void Bye()
    {
        if (TargetName != null && State == LinkState.Linked && !asleep) plugin.Replier.SendRawTo(Who, JukeboxProtocol.Prefix + "bye");
        asleep = false;
    }

    private void Hello()
    {
        sentAt = DateTime.Now;
        plugin.Replier.SendRawTo(Who, JukeboxProtocol.Prefix + "hi");
    }

    /// <summary>
    /// Their Tundy sends fresh info by itself after taking songs (0.6.3 and later), so no "hi" is needed for it.
    /// </summary>
    private bool SendsInfoAfterAck =>
        Info != null && Version.TryParse(Info.Version, out var v) && v >= new Version(0, 6, 3);

    /// <summary>Called every frame while the terminal is open: link timeout, and a refresh of their info now and then.</summary>
    public void Tick()
    {
        if (asleep)
        {
            // Reopened: ask for fresh info (which also restarts their updates).
            asleep = false;
            if (State == LinkState.Linked) Hello();
        }
        if (State == LinkState.Connecting && (DateTime.Now - sentAt).TotalSeconds > 10) State = LinkState.Failed;
        // Their Tundy sends new info by itself when its song changes; this is only a fallback (every 90 s, to keep
        // the tells between you few).
        if (State == LinkState.Linked && (DateTime.Now - InfoAt).TotalSeconds > 90 && (DateTime.Now - sentAt).TotalSeconds > 90) Hello();
        if (waitingForAck && (DateTime.Now - sentAt).TotalSeconds > 20)
        {
            waitingForAck = false;
            LastResult = "No answer about the songs. Their Tundy may have been busy; check with them, or send again.";
            LastResultOk = false;
        }
    }

    public void Refresh()
    {
        if (TargetName != null && State is LinkState.Linked or LinkState.Off or LinkState.Failed)
        {
            if (State != LinkState.Linked) State = LinkState.Connecting;
            Hello();
        }
    }

    /// <summary>Sends songs to their queue (in as few tells as possible).</summary>
    public void Send(IEnumerable<JukeboxSong> songs, bool shuffle)
    {
        if (State != LinkState.Linked) return;
        var ids = songs.Select(s => JukeboxProtocol.IdFor(s.Url)).OfType<string>().Distinct().ToList();
        if (ids.Count == 0)
        {
            LastResult = "Nothing to send: add some songs first.";
            LastResultOk = false;
            return;
        }
        foreach (var m in JukeboxProtocol.Chunk("q", ids, shuffle ? "1" : "0")) plugin.Replier.SendRawTo(Who, m);
        sentAt = DateTime.Now;
        waitingForAck = true;
        LastResult = $"Sending {ids.Count} song{(ids.Count == 1 ? "" : "s")} to {TargetName}...";
        LastResultOk = true;
    }

    /// <summary>Sends one song to their queue (no playlist needed).</summary>
    public void SendOne(JukeboxSong song) => Send(new[] { song }, shuffle: false);

    /// <summary>The songs waiting in their queue, in play order, as of <see cref="TheirQueueAt"/> (ids, see JukeboxProtocol).</summary>
    public IReadOnlyList<string> TheirQueue { get; private set; } = Array.Empty<string>();
    public DateTime TheirQueueAt { get; private set; } = DateTime.MinValue;
    public bool QueueRequested { get; private set; }
    private DateTime queueAskedAt = DateTime.MinValue;
    private bool saveWhenReceived;

    /// <summary>Asks for the songs in their queue: to show them, or (with save) to make a playlist of them.</summary>
    public void RequestQueue(bool save = false)
    {
        if (State != LinkState.Linked) return;
        if (save && TheirQueueAt > DateTime.Now.AddSeconds(-20))
        {
            // Fresh enough already: save it right away.
            SaveReceived(TheirQueue.ToList());
            return;
        }
        if (QueueRequested && (DateTime.Now - queueAskedAt).TotalSeconds < 10) { saveWhenReceived |= save; return; }
        qlistParts = null;
        saveWhenReceived |= save;
        QueueRequested = true;
        queueAskedAt = DateTime.Now;
        plugin.Replier.SendRawTo(Who, JukeboxProtocol.Prefix + "getq");
        if (save)
        {
            LastResult = $"Asking {TargetName} for their queue...";
            LastResultOk = true;
        }
    }

    private void SaveReceived(List<string> ids)
    {
        saveWhenReceived = false;
        LastResult = ids.Count == 0 ? $"{TargetName}'s queue is empty." : $"Saved {ids.Count} songs from {TargetName}'s queue.";
        LastResultOk = ids.Count > 0;
        if (ids.Count > 0) QueueReceived?.Invoke(TargetName!, ids);
    }

    /// <summary>An answer from a linked Tundy (only the player the terminal is linked to counts).</summary>
    public void OnAnswer(string name, string? world, string verb, string rest)
    {
        if (TargetName == null || !Names.Same(name, TargetName)) return;
        if (TargetWorld != null && world != null && !world.Equals(TargetWorld, StringComparison.OrdinalIgnoreCase)) return;

        switch (verb)
        {
            case "info" when JukeboxProtocol.ParseInfo(rest) is { } info:
                if (State == LinkState.Connecting) Ping = DateTime.Now - sentAt;
                // A different song or queue length: their queue list is out of date.
                if (Info != null && (Info.NowPlaying != info.NowPlaying || Info.Queued != info.Queued)) TheirQueueAt = DateTime.MinValue;
                Info = info;
                InfoAt = DateTime.Now;
                State = LinkState.Linked;
                break;
            case "off":
                State = LinkState.Off;
                break;
            case "ack":
                waitingForAck = false;
                var space = rest.IndexOf(' ');
                var kind = space < 0 ? rest : rest[..space];
                var detail = space < 0 ? "" : rest[(space + 1)..];
                (LastResult, LastResultOk) = kind switch
                {
                    "queued" => ($"Queued {detail} on {TargetName}'s side.", true),
                    "waiting" => ($"Sent {detail}: waiting for {TargetName} to approve them.", true),
                    _ => ($"Not sent: {detail}.", false),
                };
                TheirQueueAt = DateTime.MinValue; // their queue changed
                if (!SendsInfoAfterAck) Hello(); // older Tundys don't send their info by themselves
                break;
            case "qlist":
                var p = rest.Split(' ', 2);
                if (JukeboxProtocol.ParsePart(p[0]) is not { } part) return;
                if (qlistParts == null || qlistParts.Length != part.Parts) qlistParts = new string?[part.Parts];
                qlistParts[part.Part - 1] = p.Length > 1 ? p[1] : "";
                if (qlistParts.Any(x => x == null)) return;
                var ids = JukeboxProtocol.ParseIds(string.Join(',', qlistParts));
                qlistParts = null;
                TheirQueue = ids;
                TheirQueueAt = DateTime.Now;
                QueueRequested = false;
                if (saveWhenReceived) SaveReceived(ids);
                break;
        }
    }
}
