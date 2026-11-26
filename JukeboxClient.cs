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
        Ping = null;
        LastResult = "";
        State = LinkState.Connecting;
        Hello();
    }

    public void Disconnect()
    {
        State = LinkState.Idle;
        TargetName = TargetWorld = null;
        Info = null;
    }

    private void Hello()
    {
        sentAt = DateTime.Now;
        plugin.Replier.SendRawTo(Who, JukeboxProtocol.Prefix + "hi");
    }

    /// <summary>Called every frame while the terminal is open: link timeout, and a refresh of their info now and then.</summary>
    public void Tick()
    {
        if (State == LinkState.Connecting && (DateTime.Now - sentAt).TotalSeconds > 10) State = LinkState.Failed;
        if (State == LinkState.Linked && (DateTime.Now - InfoAt).TotalSeconds > 45 && (DateTime.Now - sentAt).TotalSeconds > 45) Hello();
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

    /// <summary>Asks for the songs in their queue (to save them as a playlist).</summary>
    public void RequestQueue()
    {
        if (State != LinkState.Linked) return;
        qlistParts = null;
        plugin.Replier.SendRawTo(Who, JukeboxProtocol.Prefix + "getq");
        LastResult = $"Asking {TargetName} for their queue...";
        LastResultOk = true;
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
                Hello(); // their queue changed
                break;
            case "qlist":
                var p = rest.Split(' ', 2);
                if (JukeboxProtocol.ParsePart(p[0]) is not { } part) return;
                if (qlistParts == null || qlistParts.Length != part.Parts) qlistParts = new string?[part.Parts];
                qlistParts[part.Part - 1] = p.Length > 1 ? p[1] : "";
                if (qlistParts.Any(x => x == null)) return;
                var ids = JukeboxProtocol.ParseIds(string.Join(',', qlistParts));
                qlistParts = null;
                LastResult = ids.Count == 0 ? $"{TargetName}'s queue is empty." : $"Saved {ids.Count} songs from {TargetName}'s queue.";
                LastResultOk = ids.Count > 0;
                if (ids.Count > 0) QueueReceived?.Invoke(TargetName, ids);
                break;
        }
    }
}
