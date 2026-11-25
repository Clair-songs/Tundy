using System;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace Tundy;

/// <summary>
/// Shows the song that's playing as your character's title through the Honorific plugin (if it's installed).
/// Honorific puts a title set by another plugin above the one you picked, and reports it as your title, so
/// Lightless sends it to your pairs (who need Honorific too). Clearing it brings your own Honorific title back;
/// Honorific's settings are never changed, and the title isn't saved anywhere.
/// </summary>
public sealed class HonorificTitle : IDisposable
{
    /// <summary>Honorific ignores titles longer than this (UTF-16 characters, without the 《》).</summary>
    private const int MaxLength = 32;

    private readonly Configuration config;
    private readonly IFramework framework;
    private readonly IObjectTable objects;
    private readonly Func<(string? Title, bool Shared, bool Busy)> nowPlaying;

    private readonly ICallGateSubscriber<(uint, uint)> apiVersion;
    private readonly ICallGateSubscriber<int, string, object> setTitle;
    private readonly ICallGateSubscriber<int, object> clearTitle;
    private readonly ICallGateSubscriber<string> getLocalTitle;
    private readonly ICallGateSubscriber<object> ready;
    private readonly ICallGateSubscriber<object> disposing;

    private string? applied;       // the JSON Tundy put on, or null
    private string? appliedText;   // its title text ("♪ Song"), to check it's really gone after clearing
    private DateTime idleSince = DateTime.MaxValue;
    private DateTime lastTick = DateTime.MinValue;

    /// <summary>For the settings: whether Honorific answered, and what's shown right now.</summary>
    public string Status { get; private set; } = "";

    /// <param name="nowPlaying">The song's name, whether it's shared, and whether a song is about to start.</param>
    public HonorificTitle(IDalamudPluginInterface pi, IFramework framework, IObjectTable objects, Configuration config,
                          Func<(string? Title, bool Shared, bool Busy)> nowPlaying)
    {
        this.config = config;
        this.framework = framework;
        this.objects = objects;
        this.nowPlaying = nowPlaying;

        apiVersion = pi.GetIpcSubscriber<(uint, uint)>("Honorific.ApiVersion");
        setTitle = pi.GetIpcSubscriber<int, string, object>("Honorific.SetCharacterTitle");
        clearTitle = pi.GetIpcSubscriber<int, object>("Honorific.ClearCharacterTitle");
        getLocalTitle = pi.GetIpcSubscriber<string>("Honorific.GetLocalCharacterTitle");
        ready = pi.GetIpcSubscriber<object>("Honorific.Ready");
        disposing = pi.GetIpcSubscriber<object>("Honorific.Disposing");

        // Honorific (re)loaded: it forgot the title, so put it on again on the next tick.
        ready.Subscribe(OnHonorificChanged);
        disposing.Subscribe(OnHonorificChanged);
        framework.Update += OnUpdate;
    }

    private void OnHonorificChanged()
    {
        // Honorific (re)loaded: it forgot Tundy's title. Start over on the next tick.
        applied = null;
        appliedText = null;
        phase = Phase.Idle;
    }

    /// <summary>True when Honorific is installed and answers (API 3 or newer).</summary>
    public bool Available
    {
        get
        {
            try { return apiVersion.InvokeFunc().Item1 >= 3; }
            catch { return false; }
        }
    }

    // Taking the song title off happens in steps, one per check (every second):
    //  Restoring: your own title (remembered before the first song) is put on for a moment, so Honorific writes it
    //             onto your nameplate and has the game redraw it,
    //  Verifying: then Tundy's entry is removed, and for 10 seconds Tundy checks that Honorific shows your title
    //             again, removing the song title again if it ever comes back.
    private enum Phase { Idle, Showing, Restoring, Verifying }

    private Phase phase = Phase.Idle;
    private DateTime phaseUntil;
    private string? remembered;      // your title as Honorific reported it before the first song ("" = none)
    private string? lastSongText;    // the song title last shown, to spot it coming back

    private void OnUpdate(IFramework _)
    {
        if ((DateTime.Now - lastTick).TotalSeconds < 1) return;
        lastTick = DateTime.Now;
        if (objects.LocalPlayer == null) return;

        var (title, shared, busy) = nowPlaying();
        var text = config.HonorificTitle && title != null && (shared || !config.HonorificOnlyWhenSharing)
            ? Format(title, ArtistTurn(title))
            : null;
        var wanted = text != null ? Json(text) : null;

        if (wanted != null)
        {
            idleSince = DateTime.MaxValue;
            if (wanted != applied) Apply(wanted, text!);
            return;
        }

        switch (phase)
        {
            case Phase.Restoring when DateTime.Now >= phaseUntil:
                Remove("removed the song title");
                phase = Phase.Verifying;
                phaseUntil = DateTime.Now.AddSeconds(10);
                return;
            case Phase.Restoring:
                return;
            case Phase.Verifying:
                Verify();
                return;
        }

        // Between two songs (the next one is getting ready) keep the old title for a moment, so it doesn't flicker
        // off and on. Honorific only reports a change every 5 seconds anyway.
        if (applied == null) { Status = Available ? "Honorific found. Nothing shown right now." : "Honorific isn't installed."; return; }
        if (config.HonorificTitle && busy) return;
        if (idleSince == DateTime.MaxValue) idleSince = DateTime.Now;
        if (!config.HonorificTitle || (DateTime.Now - idleSince).TotalSeconds >= 3) StartRestore();
    }

    private void Apply(string json, string text)
    {
        try
        {
            if (applied == null && phase is Phase.Idle)
            {
                // Before the first song: remember what Honorific shows as your title, to put it back afterwards.
                remembered = ReportedTitleJson() ?? "";
                Plugin.Log.Information($"Honorific: your title before the songs is {Describe(remembered)}.");
            }
            var newSong = text != lastSongText && !IsSwapOf(text);
            setTitle.InvokeAction(0, json); // object 0 = your character
            applied = json;
            appliedText = text;
            lastSongText = text;
            phase = Phase.Showing;
            if (newSong) Plugin.Log.Information($"Honorific: showing the song as your title: \"{text}\".");
            Status = "Showing the song as your title.";
        }
        catch (Exception ex)
        {
            Status = "Honorific isn't installed.";
            Plugin.Log.Warning(ex, "Honorific: couldn't set the song title");
        }
    }

    // Swapping between "♪ Song" and "by Artist" isn't a new song (not logged each time).
    private bool IsSwapOf(string text) =>
        config.HonorificShow == ShowSwap && lastSongText != null && (text.StartsWith("by ", StringComparison.Ordinal) || lastSongText.StartsWith("by ", StringComparison.Ordinal));

    /// <summary>Step 1 of taking the song title off: put your own title (or the game's) on, so the nameplate is redrawn.</summary>
    private void StartRestore()
    {
        try
        {
            var restore = string.IsNullOrEmpty(remembered)
                ? JsonSerializer.Serialize(new { Title = "", IsPrefix = false, IsOriginal = true }) // the game's own title
                : remembered;
            setTitle.InvokeAction(0, restore);
            Plugin.Log.Information($"Honorific: the music stopped; putting back {Describe(remembered)} before removing Tundy's title.");
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Honorific: couldn't put your title back"); }
        applied = null;
        appliedText = null;
        idleSince = DateTime.MaxValue;
        phase = Phase.Restoring;
        phaseUntil = DateTime.Now.AddSeconds(1.5);
        Status = "Putting your own title back...";
    }

    /// <summary>Removes Tundy's title from Honorific (an empty title, and the clear command as a backup).</summary>
    private void Remove(string what)
    {
        try { setTitle.InvokeAction(0, string.Empty); }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Honorific: the empty title failed"); }
        try { clearTitle.InvokeAction(0); }
        catch { /* the empty title above is what counts */ }
        Plugin.Log.Information($"Honorific: {what}; it now shows {Describe(ReportedTitleJson())}.");
    }

    /// <summary>Step 3: for 10 seconds, check the song title doesn't come back; remove it again if it does.</summary>
    private void Verify()
    {
        var now = ReportedTitleJson();
        if (TitleText(now) is { } t && t == lastSongText)
        {
            Remove("the song title came back, removed it again");
            Status = "Taking the song title off again...";
            return;
        }
        if (DateTime.Now < phaseUntil) return;
        phase = Phase.Idle;
        lastSongText = null;
        Status = "Your own title is back.";
        Plugin.Log.Information($"Honorific: done; your title is {Describe(now)}.");
    }

    /// <summary>Takes the song title off right away (unloading Tundy).</summary>
    private void Clear()
    {
        if (!string.IsNullOrEmpty(remembered))
        {
            try { setTitle.InvokeAction(0, remembered); } catch { /* best effort */ }
        }
        Remove("Tundy is closing, removed the song title");
        applied = null;
        appliedText = null;
        phase = Phase.Idle;
    }

    /// <summary>The title Honorific reports for your character right now, as its JSON ("" for none); null if it can't tell.</summary>
    private string? ReportedTitleJson()
    {
        try { return getLocalTitle.InvokeFunc() ?? ""; }
        catch { return null; }
    }

    private static string? TitleText(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch { return null; }
    }

    private static string Describe(string? json) =>
        json == null ? "(Honorific didn't say)"
        : TitleText(json) is { Length: > 0 } t ? $"\"{t}\""
        : "no Honorific title (the game's own)";

    /// <summary>What the title shows: 0 the song's name, 1 the song's name then the artist, 2 swap between the two.</summary>
    public const int ShowSong = 0, ShowSongThenArtist = 1, ShowSwap = 2;

    /// <summary>"Artist - Song" (how Tundy names songs) -> (song, artist); artist is null when there's none.</summary>
    private static (string Song, string? Artist) Split(string songTitle)
    {
        var text = new string(songTitle.Where(c => !char.IsControl(c)).ToArray()).Trim();
        var dash = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dash <= 0 || dash + 3 >= text.Length) return (text, null);
        return (text[(dash + 3)..].Trim(), text[..dash].Trim());
    }

    /// <summary>
    /// The title for a song, cut to Honorific's 32-character limit: "♪ Song", "♪ Song · Artist", or (swapping)
    /// "♪ Song" and "by Artist" taking turns, with <paramref name="artistTurn"/> picking the second one.
    /// </summary>
    public string Format(string songTitle, bool artistTurn = false)
    {
        var (song, artist) = Split(songTitle);
        var text = config.HonorificShow switch
        {
            ShowSongThenArtist when artist != null => $"♪ {song} · {artist}",
            ShowSwap when artist != null && artistTurn => $"by {artist}",
            _ => $"♪ {song}",
        };
        if (text.Length <= MaxLength) return text;
        var cut = MaxLength - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--; // don't split an emoji or other two-part character
        return text[..cut].TrimEnd() + "…";
    }

    /// <summary>True when the song has an artist to swap to (for the settings preview).</summary>
    public static bool HasArtist(string songTitle) => Split(songTitle).Artist != null;

    // Swapping: which song the turns started with, and when.
    private string? turnSong;
    private DateTime turnsSince;

    private bool ArtistTurn(string songTitle)
    {
        if (config.HonorificShow != ShowSwap) return false;
        if (turnSong != songTitle)
        {
            turnSong = songTitle;
            turnsSince = DateTime.Now; // a new song starts with its name
        }
        var seconds = Math.Clamp(config.HonorificSwapSeconds, 5, 60);
        return (int)((DateTime.Now - turnsSince).TotalSeconds / seconds) % 2 == 1;
    }

    private string Json(string title)
    {
        static object? Rgb(Vector3? c) => c is { } v ? new { v.X, v.Y, v.Z } : null;
        return JsonSerializer.Serialize(new
        {
            Title = title,
            IsPrefix = config.HonorificAbove,
            IsOriginal = false,
            Color = Rgb(config.HonorificUseColor ? config.HonorificColor : null),
            Glow = Rgb(config.HonorificUseGlow ? config.HonorificGlow : null),
        });
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        ready.Unsubscribe(OnHonorificChanged);
        disposing.Unsubscribe(OnHonorificChanged);
        // Also mid-way through putting your title back (Tundy's entry is still set then).
        if (applied != null || phase is Phase.Restoring or Phase.Verifying)
        {
            try { framework.RunOnFrameworkThread(Clear).Wait(TimeSpan.FromSeconds(2)); }
            catch { /* ignored */ }
        }
    }
}
