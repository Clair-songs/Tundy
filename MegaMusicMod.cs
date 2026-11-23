using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Tundy;

/// <summary>
/// The music mod songs play through (by default Yue &amp; Lu's Mega Music Mod), the way SoundyQ does it:
/// the mod's DJ dance on /cheerjumpred plays one game sound file ("sound/lolo.scd"). Tundy points that file at
/// the requested song (in its own "Tundy Song" mod, which wins over the music mod), redraws you so Lightless
/// Sync sends it to your pairs, and does the emote. The music mod's own files are never changed.
/// </summary>
public sealed class MegaMusicMod
{
    public const string RecommendedMod = "[Yue & Lu's] Mega Music Mod";
    public const string RecommendedRoute = "sound/lolo.scd";
    public const string RecommendedEmote = "/cheerjumpred";

    private readonly IDalamudPluginInterface pi;
    private readonly IObjectTable objects;
    private readonly Configuration config;

    private (bool Installed, bool Enabled) state;
    private DateTime stateAt = DateTime.MinValue;
    private readonly Dictionary<string, byte[]> templates = new(StringComparer.OrdinalIgnoreCase);

    public MegaMusicMod(IDalamudPluginInterface pi, IObjectTable objects, Configuration config)
    {
        this.pi = pi;
        this.objects = objects;
        this.config = config;
    }

    /// <summary>The emote that plays the song, if it's a plain emote command; otherwise the recommended one.</summary>
    public static string SafeEmote(string command)
    {
        var c = command.Trim().ToLowerInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(c, "^/[a-z0-9]{2,30}$") ? c : RecommendedEmote;
    }

    /// <summary>The game sound files that carry the song (at least the recommended one).</summary>
    public IReadOnlyList<string> Routes =>
        config.QueueScdRoutes.Where(r => r.EndsWith(".scd", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            is { Count: > 0 } list ? list : new List<string> { RecommendedRoute };

    /// <summary>Penumbra's mods: folder name -> display name. Empty if Penumbra isn't available.</summary>
    public Dictionary<string, string> ModList()
    {
        try { return new GetModList(pi).Invoke(); }
        catch { return new Dictionary<string, string>(); }
    }

    /// <summary>Whether the target mod is installed and turned on in your collection (checked every 3 s).</summary>
    public (bool Installed, bool Enabled) State()
    {
        if ((DateTime.Now - stateAt).TotalSeconds < 3) return state;
        stateAt = DateTime.Now;
        dance = null;
        try
        {
            if (!ModList().ContainsKey(config.QueueTargetMod)) return state = (false, false);
            if (objects.LocalPlayer == null) return state = (true, false);
            var (_, _, collection) = new GetCollectionForObject(pi).Invoke(0);
            var (ec, settings) = new GetCurrentModSettings(pi).Invoke(collection.Id, config.QueueTargetMod);
            state = (true, ec == PenumbraApiEc.Success && settings is { } s && s.Item1);
            if (state.Enabled)
            {
                try { dance = WrongDance(collection.Id); }
                catch (Exception ex) { Plugin.Log.Debug(ex, "Couldn't read the music mod's dance"); }
            }
            return state;
        }
        catch
        {
            return state = (false, false);
        }
    }

    /// <summary>Problems that stop songs from playing through the music mod, in plain words.</summary>
    public List<string> SetupProblems()
    {
        var problems = new List<string>();
        var (installed, enabled) = State();
        if (!installed)
            problems.Add(IsRecommended && InstalledMega() is { } other
                ? $"\"{config.QueueTargetMod}\" isn't in Penumbra, but \"{other}\" is. Use that one instead."
                : $"\"{config.QueueTargetMod}\" isn't in Penumbra. Install it, or pick another music mod below.");
        else if (!enabled) problems.Add($"\"{config.QueueTargetMod}\" is turned off in your collection."
                                        + (IsRecommended ? " Click \"Set it up for me\"." : " Turn it on in Penumbra."));
        else if (dance is { } d)
            problems.Add($"The music mod's dance is \"{d}\", not \"{DjDance}\", so the songs won't play on {RecommendedEmote}. Click \"Set it up for me\".");
        return problems;
    }

    private const string DjDance = "Dj (Cheer Jump)";

    // The Mega Music Mod's dance when it isn't the DJ one (null when it is, or when it can't be told).
    private string? dance;

    /// <summary>The Mega Music Mod's "Dance Animations" choice, if it isn't the DJ dance that plays the song slot.</summary>
    private string? WrongDance(Guid collection)
    {
        if (!IsRecommended || SafeEmote(config.QueueEmoteCommand) != RecommendedEmote) return null;
        var (ec, settings) = new GetCurrentModSettings(pi).Invoke(collection, config.QueueTargetMod);
        if (ec != PenumbraApiEc.Success || settings is not { } s || !s.Item3.TryGetValue("Dance Animations", out var picked)) return null;
        if (picked.Any(p => p.Equals(DjDance, StringComparison.OrdinalIgnoreCase))) return null;
        return picked.Count > 0 ? string.Join(", ", picked) : "none";
    }

    /// <summary>The Mega Music Mod the "Use Mega Music Mod" button switches to: the installed version.</summary>
    public string PreferredMega => InstalledMega() ?? RecommendedMod;

    /// <summary>The Mega Music Mod, or another version of it (for example the "No songs Ver.").</summary>
    public bool IsRecommended => config.QueueTargetMod.StartsWith(RecommendedMod, StringComparison.OrdinalIgnoreCase);

    /// <summary>Forces the next <see cref="State"/> call to check again.</summary>
    public void Recheck()
    {
        stateAt = DateTime.MinValue;
        lock (templates) templates.Clear(); // the mod or its files may have changed
    }

    private string? TargetFolder()
    {
        try
        {
            var root = new GetModDirectory(pi).Invoke();
            var dir = Path.Combine(root, config.QueueTargetMod);
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// The music mod's file paths (game path -> file in the mod) from its option files: meta.json (newer mods keep
    /// their options there), default_mod.json and group_*.json.
    /// </summary>
    internal static IEnumerable<(string GamePath, string File)> ModFiles(string modDir)
    {
        var files = new List<string>();
        foreach (var name in new[] { "meta.json", "default_mod.json" })
            if (File.Exists(Path.Combine(modDir, name))) files.Add(Path.Combine(modDir, name));
        files.AddRange(Directory.GetFiles(modDir, "group_*.json"));

        foreach (var file in files)
        {
            List<(string, string)> found = new();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                Walk(doc.RootElement, found);
            }
            catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't read {file}"); }
            foreach (var f in found) yield return f;
        }

        static void Walk(JsonElement e, List<(string, string)> found)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name == "Files" && p.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var f in p.Value.EnumerateObject())
                            if (f.Value.ValueKind == JsonValueKind.String)
                                found.Add((f.Name.Replace('\\', '/'), f.Value.GetString()!));
                    }
                    else
                    {
                        Walk(p.Value, found);
                    }
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in e.EnumerateArray()) Walk(x, found);
            }
        }
    }

    /// <summary>The music mod's sound files, most used first (the song slot is used by almost every option).</summary>
    public List<(string Route, int Uses)> ScanRoutes() => TargetFolder() is { } dir ? ScanRoutes(dir) : new();

    /// <summary>The sound files a mod puts sounds on, most used first.</summary>
    public static List<(string Route, int Uses)> ScanRoutes(string dir)
    {
        return ModFiles(dir)
            .Where(f => f.GamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => f.GamePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(g => g.Item2)
            .ThenBy(g => g.Key)
            .Take(30)
            .ToList();
    }

    /// <summary>
    /// A sound file that already goes on <paramref name="route"/>, used as the template for songs (it has the
    /// right sound settings for that slot): one of the music mod's own files for it, or else whatever the game
    /// uses for it right now. Called off the framework thread, so it mostly just reads files.
    /// </summary>
    public byte[] TemplateFor(string route, string ownModFolder)
    {
        lock (templates)
            if (templates.TryGetValue(route, out var cached)) return cached;

        byte[]? bytes = null;
        if (TargetFolder() is { } dir)
        {
            foreach (var (gamePath, file) in ModFiles(dir))
            {
                if (!gamePath.Equals(route, StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                bytes = File.ReadAllBytes(full);
                break;
            }
        }

        if (bytes == null)
        {
            try
            {
                var resolved = new ResolvePlayerPath(pi).Invoke(route);
                if (Path.IsPathRooted(resolved) && File.Exists(resolved)
                    && !resolved.Contains(Path.DirectorySeparatorChar + ownModFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    bytes = File.ReadAllBytes(resolved);
            }
            catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't resolve {route}"); }
        }

        // Music mods that ship without their songs (like the Mega Music Mod's "No songs Ver.") have no file on the
        // slot. Then the sound settings of the Mega Music Mod's song slot, built into Tundy, are used.
        if (bytes == null && route.Equals(RecommendedRoute, StringComparison.OrdinalIgnoreCase) && BuiltInTemplate() is { } builtIn)
        {
            Plugin.Log.Information($"\"{config.QueueTargetMod}\" has no file on {route}; using Tundy's built-in song slot settings");
            bytes = builtIn;
        }

        if (bytes == null)
            throw new FileNotFoundException($"No sound file for \"{route}\" found in \"{config.QueueTargetMod}\". Check the music mod and sound route in the Sharing tab.");

        lock (templates) templates[route] = bytes;
        return bytes;
    }

    /// <summary>The Mega Music Mod song slot's sound settings (with 2 seconds of silence), shipped with Tundy.</summary>
    private static byte[]? BuiltInTemplate()
    {
        using var stream = typeof(MegaMusicMod).Assembly.GetManifestResourceStream("Tundy.song-slot-template.scd");
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The installed Mega Music Mod: the full one if it's there, otherwise another version of it (for example the
    /// "No songs Ver."), or null.
    /// </summary>
    public string? InstalledMega()
    {
        var mods = ModList().Keys.ToList();
        if (mods.Contains(RecommendedMod)) return RecommendedMod;
        return mods.Where(m => m.StartsWith(RecommendedMod, StringComparison.OrdinalIgnoreCase)).OrderBy(m => m.Length).FirstOrDefault();
    }

    /// <summary>
    /// The file the game plays for <paramref name="route"/> right now, unless it's Tundy's own (or none).
    /// Must run on the framework thread.
    /// </summary>
    public string? CurrentFileFor(string route, string ownModFolder)
    {
        try
        {
            var resolved = new ResolvePlayerPath(pi).Invoke(route);
            return Path.IsPathRooted(resolved) && File.Exists(resolved)
                   && !resolved.Contains(Path.DirectorySeparatorChar + ownModFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? resolved
                : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"Couldn't resolve {route}");
            return null;
        }
    }

    /// <summary>
    /// Turns the Mega Music Mod on in your collection with its DJ dance on Cheer Jump: Red and the DJ deck that
    /// fits your race. Must run on the framework thread.
    /// </summary>
    public string SetUp()
    {
        var me = objects.LocalPlayer;
        if (me == null) return "Log in to a character first.";

        var (_, _, collection) = new GetCollectionForObject(pi).Invoke(0);
        var setOptions = new TrySetModSettings(pi);

        // Race/gender from the character's appearance: 1 Hyur, 2 Elezen, 3 Lalafell, 4 Miqo'te, 5 Roegadyn,
        // 6 Au Ra, 7 Hrothgar, 8 Viera; gender 0 = male.
        var race = me.Customize[0];
        var male = me.Customize[1] == 0;
        var deck = race switch
        {
            5 or 7 => "DJ - Dj Deck - Roge & Roth",
            3 => "DJ - Dj Deck - Lala",
            6 when male => "DJ - Dj Deck - Aura M",
            8 when male => "DJ - Dj Deck - Viera M",
            _ => "DJ - Dj Deck - Hyur, Miqo, Viera, Elez & Aura",
        };

        var mod = IsRecommended ? config.QueueTargetMod : PreferredMega;
        var ec = new TrySetMod(pi).Invoke(collection.Id, mod, true);
        if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
            return $"Penumbra couldn't turn on the Mega Music Mod ({ec}).";

        var dance = setOptions.Invoke(collection.Id, mod, "Dance Animations", new[] { DjDance });

        // "VFX Options" lets you pick several at once: keep what you already have on and add the deck. Only the
        // other DJ decks come off, since only one fits your character.
        var current = new List<string>();
        try
        {
            var (sec, settings) = new GetCurrentModSettings(pi).Invoke(collection.Id, mod);
            if (sec == PenumbraApiEc.Success && settings is { } s && s.Item3.TryGetValue("VFX Options", out var on))
                current = on.ToList();
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, "Couldn't read the Mega Music Mod's VFX options"); }
        var wanted = current.Where(o => !o.StartsWith("DJ - Dj Deck", StringComparison.OrdinalIgnoreCase)).Append(deck).Distinct().ToArray();
        var vfx = setOptions.Invoke(collection.Id, mod, "VFX Options", wanted);
        new RedrawObject(pi).Invoke(0);
        Recheck();
        Plugin.Log.Information($"Mega Music Mod on in \"{collection.Name}\": dance={dance}, vfx={vfx} ({deck})");

        return dance is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged
            ? $"Mega Music Mod is on in \"{collection.Name}\" with the DJ dance on /cheerjumpred and the \"{deck}\" deck."
            : $"Turned the mod on, but couldn't pick the DJ dance ({dance}). Pick \"Dj (Cheer Jump)\" in Penumbra.";
    }
}
