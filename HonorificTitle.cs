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
    private readonly ICallGateSubscriber<object> ready;
    private readonly ICallGateSubscriber<object> disposing;

    private string? applied;       // the JSON Tundy put on, or null
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
        ready = pi.GetIpcSubscriber<object>("Honorific.Ready");
        disposing = pi.GetIpcSubscriber<object>("Honorific.Disposing");

        // Honorific (re)loaded: it forgot the title, so put it on again on the next tick.
        ready.Subscribe(OnHonorificChanged);
        disposing.Subscribe(OnHonorificChanged);
        framework.Update += OnUpdate;
    }

    private void OnHonorificChanged() => applied = null;

    /// <summary>True when Honorific is installed and answers (API 3 or newer).</summary>
    public bool Available
    {
        get
        {
            try { return apiVersion.InvokeFunc().Item1 >= 3; }
            catch { return false; }
        }
    }

    private void OnUpdate(IFramework _)
    {
        if ((DateTime.Now - lastTick).TotalSeconds < 1) return;
        lastTick = DateTime.Now;
        if (objects.LocalPlayer == null) return;

        var (title, shared, busy) = nowPlaying();
        var wanted = config.HonorificTitle && title != null && (shared || !config.HonorificOnlyWhenSharing)
            ? Json(Format(title))
            : null;

        if (wanted != null)
        {
            idleSince = DateTime.MaxValue;
            if (wanted != applied) Apply(wanted);
            return;
        }

        // Between two songs (the next one is getting ready) keep the old title for a moment, so it doesn't flicker
        // off and on. Honorific only reports a change every 5 seconds anyway.
        if (applied == null) { Status = Available ? "Honorific found. Nothing shown right now." : "Honorific isn't installed."; return; }
        if (config.HonorificTitle && busy) return;
        if (idleSince == DateTime.MaxValue) idleSince = DateTime.Now;
        if (!config.HonorificTitle || (DateTime.Now - idleSince).TotalSeconds >= 3) Clear();
    }

    private void Apply(string json)
    {
        try
        {
            setTitle.InvokeAction(0, json); // object 0 = your character
            applied = json;
            Status = "Showing the song as your title.";
        }
        catch (Exception ex)
        {
            Status = "Honorific isn't installed.";
            Plugin.Log.Debug(ex, "Couldn't set the Honorific title");
        }
    }

    private void Clear()
    {
        try { clearTitle.InvokeAction(0); }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Couldn't clear the Honorific title"); }
        applied = null;
        idleSince = DateTime.MaxValue;
    }

    /// <summary>"♪ Song" (or "♪ Artist - Song"), cut to Honorific's 32-character limit.</summary>
    public string Format(string songTitle)
    {
        var text = new string(songTitle.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (!config.HonorificWithArtist)
        {
            var dash = text.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0 && dash + 3 < text.Length) text = text[(dash + 3)..].Trim();
        }
        text = "♪ " + text;
        if (text.Length <= MaxLength) return text;
        var cut = MaxLength - 1;
        if (char.IsHighSurrogate(text[cut - 1])) cut--; // don't split an emoji or other two-part character
        return text[..cut].TrimEnd() + "…";
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
        if (applied != null)
        {
            try { framework.RunOnFrameworkThread(Clear).Wait(TimeSpan.FromSeconds(2)); }
            catch { /* ignored */ }
        }
    }
}
