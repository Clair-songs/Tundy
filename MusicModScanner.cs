using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tundy;

/// <summary>An emote the game knows, found from one of its animation keys.</summary>
public sealed record EmoteInfo(uint Id, string Command, string Name);

/// <summary>
/// One dance in a music mod that plays a song: the mod option that turns it on, the emote it's on, and the song
/// file (game path) its animation plays, which is where Tundy puts the requested song.
/// </summary>
/// <param name="Route">The game sound path the dance plays, e.g. "sound/moonlight.scd".</param>
/// <param name="IsMusic">The mod's own file there is streamed Vorbis music (like the Mega Music Mod's);
/// false for a short MS-ADPCM sound effect, which needs songs built the same way.</param>
public sealed record DanceSong(string Group, string Option, bool MultiGroup, EmoteInfo? Emote, string Route, long FileBytes, bool IsMusic)
{
    /// <summary>Saved in the settings to remember the picked dance.</summary>
    public string Key => $"{Group}\u0001{Option}";

    public string Label => $"{Option}  ({Group})";
}

/// <summary>What a scan of a mod found.</summary>
public sealed record MusicModScan(string ModFolder, IReadOnlyList<DanceSong> Songs, int AnimatedOptions, DateTime At);

/// <summary>
/// Reads a Penumbra mod and finds its dances that play a song, the same way it was worked out by hand for the
/// Mega Music Mod, Starlit and Wall Slams: each option's animation files (.pap/.tmb) name the sound files they play;
/// the ones the mod itself supplies and that are big enough to be songs are the song slots. Only reads files.
/// </summary>
public static partial class MusicModScanner
{
    /// <summary>Sound files at least this big count as songs (sound effects are much smaller).</summary>
    private const long MinSongBytes = 250 * 1024;

    [GeneratedRegex(@"sound/[\w/\.\-]+?\.scd", RegexOptions.IgnoreCase)]
    private static partial Regex SoundPath();

    private sealed record ModOption(string Group, string Name, bool Multi, Dictionary<string, string> Files);

    /// <param name="emoteForKey">The emote for an animation key like "emote/dance03_loop", or null.</param>
    public static MusicModScan Scan(string modDir, Func<string, EmoteInfo?> emoteForKey)
    {
        var options = ReadOptions(modDir);

        // Every sound file the mod supplies anywhere: game path -> file in the mod.
        var supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in options)
            foreach (var (gamePath, file) in o.Files)
                if (gamePath.EndsWith(".scd", StringComparison.OrdinalIgnoreCase) && !supplied.ContainsKey(gamePath))
                    supplied[gamePath] = file;

        var songs = new List<DanceSong>();
        var animated = 0;
        var soundCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in options)
        {
            var anims = o.Files.Where(f => f.Key.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)
                                        || f.Key.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)).ToList();
            if (anims.Count == 0) continue;
            animated++;

            // Which emote: the first animation key the game knows as an emote's animation.
            EmoteInfo? emote = null;
            foreach (var (gamePath, _) in anims)
            {
                if (AnimationKey(gamePath) is { } key && emoteForKey(key) is { } e) { emote = e; break; }
            }

            // Which sounds the animations play.
            var played = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, file) in anims)
            {
                if (!soundCache.TryGetValue(file, out var sounds))
                {
                    sounds = ReadSoundPaths(Path.Combine(modDir, file.Replace('/', Path.DirectorySeparatorChar)));
                    soundCache[file] = sounds;
                }
                foreach (var s in sounds) played.Add(s);
            }

            // The song: a played sound the mod supplies, big enough to be music. The biggest wins (a dance can
            // also play a short start sound).
            DanceSong? best = null;
            foreach (var route in played)
            {
                var file = o.Files.TryGetValue(route, out var own) ? own : supplied.GetValueOrDefault(route);
                if (file == null) continue;
                var full = Path.Combine(modDir, file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                var bytes = new FileInfo(full).Length;
                if (bytes < MinSongBytes) continue;
                if (best != null && best.FileBytes >= bytes) continue;
                best = new DanceSong(o.Group, o.Name, o.Multi, emote, route.ToLowerInvariant(), bytes, IsVorbis(full));
            }
            if (best != null) songs.Add(best);
        }

        return new MusicModScan(modDir, songs, animated, DateTime.Now);
    }

    /// <summary>"chara/human/c0101/animation/a0001/bt_common/emote/dance03_loop.pap" -> "emote/dance03_loop".</summary>
    public static string? AnimationKey(string gamePath)
    {
        var p = gamePath.Replace('\\', '/').ToLowerInvariant();
        string? key = null;
        var bt = p.IndexOf("/bt_common/", StringComparison.Ordinal);
        if (bt >= 0) key = p[(bt + "/bt_common/".Length)..];
        else if (p.StartsWith("chara/action/", StringComparison.Ordinal)) key = p["chara/action/".Length..];
        if (key == null) return null;
        var dot = key.LastIndexOf('.');
        return dot > 0 ? key[..dot] : key;
    }

    private static string[] ReadSoundPaths(string file)
    {
        try
        {
            if (!File.Exists(file)) return Array.Empty<string>();
            var text = Encoding.ASCII.GetString(File.ReadAllBytes(file));
            return SoundPath().Matches(text).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>True when the .scd's first sound is Vorbis (format 6), like the Mega Music Mod's songs.</summary>
    public static bool IsVorbis(string scdFile)
    {
        try
        {
            using var fs = File.OpenRead(scdFile);
            var head = new byte[Math.Min(fs.Length, 8192)];
            fs.ReadExactly(head);
            return IsVorbis(head);
        }
        catch { return true; }
    }

    /// <summary>Same, from the file's bytes (or at least its first few KB).</summary>
    public static bool IsVorbis(byte[] scd)
    {
        if (scd.Length < 0x50 || Encoding.ASCII.GetString(scd, 0, 8) != "SEDBSSCF") return true;
        var table = BitConverter.ToInt32(scd, 0x3C);
        if (table < 0 || table + 4 > scd.Length) return true;
        var first = BitConverter.ToInt32(scd, table);
        if (first < 0 || first + 16 > scd.Length) return true;
        return BitConverter.ToInt32(scd, first + 12) == 6;
    }

    /// <summary>The mod's options from meta.json (newer mods) or group_*.json, plus its always-on files.</summary>
    private static List<ModOption> ReadOptions(string modDir)
    {
        var list = new List<ModOption>();
        var docs = new List<JsonElement>();
        var keep = new List<JsonDocument>();
        try
        {
            void Load(string file)
            {
                if (!File.Exists(file)) return;
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                keep.Add(doc);
                docs.Add(doc.RootElement);
            }

            var meta = Path.Combine(modDir, "meta.json");
            Load(meta);
            foreach (var g in Directory.GetFiles(modDir, "group_*.json").OrderBy(f => f)) Load(g);
            Load(Path.Combine(modDir, "default_mod.json"));

            foreach (var root in docs)
            {
                if (root.ValueKind != JsonValueKind.Object) continue;
                // A group file, or meta.json's "Groups" list (FileVersion 4).
                if (root.TryGetProperty("Options", out _)) AddGroup(root, list);
                if (root.TryGetProperty("Groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
                    foreach (var g in groups.EnumerateArray()) AddGroup(g, list);
                // Always-on files: meta.json "DefaultData", or default_mod.json itself.
                if (root.TryGetProperty("DefaultData", out var def)) AddFiles("(always on)", "(always on)", false, def, list);
                else if (root.TryGetProperty("Files", out _) && !root.TryGetProperty("Options", out _)) AddFiles("(always on)", "(always on)", false, root, list);
            }
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't read the options of {modDir}"); }
        finally { foreach (var d in keep) d.Dispose(); }
        return list;
    }

    private static void AddGroup(JsonElement group, List<ModOption> list)
    {
        if (!group.TryGetProperty("Options", out var options) || options.ValueKind != JsonValueKind.Array) return;
        var name = group.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var multi = group.TryGetProperty("Type", out var t) && t.ValueKind == JsonValueKind.String
                    && t.GetString()!.Equals("Multi", StringComparison.OrdinalIgnoreCase);
        foreach (var o in options.EnumerateArray())
        {
            var optionName = o.TryGetProperty("Name", out var on) && on.ValueKind == JsonValueKind.String ? on.GetString()! : "";
            AddFiles(name, optionName, multi, o, list);
        }
    }

    private static void AddFiles(string group, string option, bool multi, JsonElement holder, List<ModOption> list)
    {
        if (!holder.TryGetProperty("Files", out var files) || files.ValueKind != JsonValueKind.Object) return;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files.EnumerateObject())
            if (f.Value.ValueKind == JsonValueKind.String)
                map[f.Name.Replace('\\', '/')] = f.Value.GetString()!.Replace('\\', '/');
        if (map.Count > 0) list.Add(new ModOption(group, option, multi, map));
    }
}
