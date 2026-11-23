using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace Tundy;

/// <summary>Where an imported sound comes from: a YouTube link or a file on your PC.</summary>
public sealed record ImportSource(bool YouTube, string Value);

/// <summary>A song added to a mod with Tundy.</summary>
public sealed record ImportedSong(string Name, string File, string Description);

/// <summary>
/// Soundy's import tools, Tundy style:
/// - Import: adds a song (YouTube or a file on your PC) to any Penumbra mod as a new option in a "Tundy imports"
///   group, on the sound file(s) you pick, e.g. sound/lolo.scd in the Mega Music Mod (the DJ dance's song).
///   The song is converted like the mod's own sound there (Vorbis music or ADPCM effect), with loudness and EQ.
/// - Sound replacer: puts a song on any game sound file through Tundy's own "Tundy Sounds" mod.
/// Before Tundy changes another mod's files for the first time it copies them to "tundy-backup" in that mod.
/// </summary>
public sealed class ModImporter
{
    public const string GroupName = "Tundy imports";
    public const string SoundsMod = "Tundy Sounds";
    private const int SoundsModPriority = 9000; // under Tundy Song (10000), over normal mods

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep names like "Ø" readable
    };

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IDataManager data;
    private readonly YtDlp ytDlp;
    private readonly FfmpegTool ffmpeg;
    private readonly Configuration config;
    private readonly SemaphoreSlim busy = new(1, 1);

    public string Status { get; private set; } = "";
    public bool StatusOk { get; private set; } = true;
    public bool Busy => busy.CurrentCount == 0;

    public ModImporter(IDalamudPluginInterface pi, IFramework framework, IDataManager data, YtDlp ytDlp, FfmpegTool ffmpeg, Configuration config)
    {
        this.pi = pi;
        this.framework = framework;
        this.data = data;
        this.ytDlp = ytDlp;
        this.ffmpeg = ffmpeg;
        this.config = config;
    }

    private string Root => new GetModDirectory(pi).Invoke();

    public string? ModFolder(string mod)
    {
        try
        {
            var dir = Path.Combine(Root, mod);
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ importing into a mod

    /// <summary>Adds a song to <paramref name="mod"/> as an option of the "Tundy imports" group. Runs in the background.</summary>
    public void Import(ImportSource source, string name, string mod, IReadOnlyList<string> routes, bool select) =>
        Run(async ct =>
        {
            if (routes.Count == 0) throw new InvalidOperationException("Pick at least one sound file in the mod.");
            var modDir = ModFolder(mod) ?? throw new DirectoryNotFoundException($"\"{mod}\" isn't in your Penumbra mod folder.");
            var (audio, title, temp) = await GetAudioAsync(source, ct);
            try
            {
                var songName = string.IsNullOrWhiteSpace(name) ? title : name.Trim();
                var template = TemplateFor(modDir, routes[0]);
                Status = $"Converting \"{songName}\"...";
                var scd = await Task.Run(() => Convert(template, audio, ct), ct);

                var rel = Path.Combine("tundy", SafeFileName(songName) + ".scd");
                Directory.CreateDirectory(Path.Combine(modDir, "tundy"));
                Backup(modDir);
                await File.WriteAllBytesAsync(Path.Combine(modDir, rel), scd, ct);
                AddOption(modDir, songName, routes.ToDictionary(r => r, _ => rel),
                    source.YouTube ? $"Imported with Tundy from {source.Value}" : $"Imported with Tundy from {Path.GetFileName(source.Value)}");

                await framework.RunOnFrameworkThread(() =>
                {
                    var ec = new ReloadMod(pi).Invoke(mod, string.Empty);
                    if (ec != PenumbraApiEc.Success) throw new InvalidOperationException($"Penumbra couldn't reload \"{mod}\" ({ec}).");
                    if (select)
                    {
                        var (_, _, collection) = new GetCollectionForObject(pi).Invoke(0);
                        new TrySetMod(pi).Invoke(collection.Id, mod, true);
                        new TrySetModSettings(pi).Invoke(collection.Id, mod, GroupName, new[] { songName });
                        new RedrawObject(pi).Invoke(0);
                    }
                });
                Done(true, $"Added \"{songName}\" to \"{mod}\" ({scd.Length / 1024} KB){(select ? " and picked it" : "")}.");
            }
            finally
            {
                if (temp) TryDelete(audio);
            }
        });

    /// <summary>Songs added to <paramref name="mod"/> with Tundy.</summary>
    public List<ImportedSong> Imported(string mod)
    {
        var list = new List<ImportedSong>();
        if (ModFolder(mod) is not { } modDir) return list;
        foreach (var group in Groups(modDir).Where(g => g.Group["Name"]?.GetValue<string>() == GroupName))
            foreach (var o in (group.Group["Options"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            {
                var n = o["Name"]?.GetValue<string>() ?? "";
                if (n == "Off") continue;
                var file = (o["Files"] as JsonObject)?.Select(f => f.Value?.GetValue<string>() ?? "").FirstOrDefault() ?? "";
                list.Add(new ImportedSong(n, file, o["Description"]?.GetValue<string>() ?? ""));
            }
        return list;
    }

    /// <summary>Picks an imported song in the mod (or "Off").</summary>
    public void Use(string mod, string option) =>
        Run(async _ =>
        {
            await framework.RunOnFrameworkThread(() =>
            {
                var (_, _, collection) = new GetCollectionForObject(pi).Invoke(0);
                new TrySetMod(pi).Invoke(collection.Id, mod, true);
                var ec = new TrySetModSettings(pi).Invoke(collection.Id, mod, GroupName, new[] { option });
                new RedrawObject(pi).Invoke(0);
                if (ec is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged))
                    throw new InvalidOperationException($"Penumbra couldn't pick it ({ec}).");
            });
            Done(true, $"Picked \"{option}\" in \"{mod}\".");
        });

    /// <summary>Removes an imported song from the mod and deletes its file.</summary>
    public void Remove(string mod, string option) =>
        Run(async _ =>
        {
            var modDir = ModFolder(mod) ?? throw new DirectoryNotFoundException($"\"{mod}\" isn't in your Penumbra mod folder.");
            string? file = null;
            EditGroup(modDir, options =>
            {
                var o = options.OfType<JsonObject>().FirstOrDefault(x => x["Name"]?.GetValue<string>() == option);
                if (o == null) return;
                file = (o["Files"] as JsonObject)?.Select(f => f.Value?.GetValue<string>()).FirstOrDefault();
                options.Remove(o);
            }, create: false);
            if (file != null && file.StartsWith("tundy" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                TryDelete(Path.Combine(modDir, file));
            await framework.RunOnFrameworkThread(() => new ReloadMod(pi).Invoke(mod, string.Empty));
            Done(true, $"Removed \"{option}\" from \"{mod}\".");
        });

    // ------------------------------------------------------------------ sound replacer ("Tundy Sounds" mod)

    /// <summary>Puts a song on any game sound file (e.g. an emote's sound) through the "Tundy Sounds" mod.</summary>
    public void ReplaceSound(ImportSource source, string gamePath) =>
        Run(async ct =>
        {
            gamePath = gamePath.Trim().Replace('\\', '/').ToLowerInvariant();
            if (!gamePath.EndsWith(".scd", StringComparison.Ordinal)) throw new InvalidOperationException("Pick a game sound file (ending in .scd).");
            var template = data.GetFile(gamePath)?.Data
                           ?? throw new FileNotFoundException($"\"{gamePath}\" isn't a sound file in the game.");
            var (audio, title, temp) = await GetAudioAsync(source, ct);
            try
            {
                Status = $"Converting \"{title}\"...";
                var scd = await Task.Run(() => Convert(template, audio, ct), ct);
                var modDir = await framework.RunOnFrameworkThread(EnsureSoundsMod);
                var rel = Path.Combine("sounds", gamePath.Replace('/', '_'));
                Directory.CreateDirectory(Path.Combine(modDir, "sounds"));
                await File.WriteAllBytesAsync(Path.Combine(modDir, rel), scd, ct);

                var files = ReadDefaultFiles(modDir);
                files[gamePath] = rel;
                SongSharer.WriteDefaultMod(modDir, files);
                await ApplySoundsModAsync();
                Done(true, $"\"{gamePath}\" now plays \"{title}\" ({scd.Length / 1024} KB).");
            }
            finally
            {
                if (temp) TryDelete(audio);
            }
        });

    /// <summary>Game sound files the "Tundy Sounds" mod replaces: game path -> file in the mod.</summary>
    public Dictionary<string, string> ReplacedSounds() =>
        ModFolder(SoundsMod) is { } dir ? ReadDefaultFiles(dir) : new Dictionary<string, string>();

    public void RestoreSound(string gamePath) =>
        Run(async _ =>
        {
            if (ModFolder(SoundsMod) is not { } dir) return;
            var files = ReadDefaultFiles(dir);
            if (files.Remove(gamePath, out var rel)) TryDelete(Path.Combine(dir, rel));
            SongSharer.WriteDefaultMod(dir, files);
            await ApplySoundsModAsync();
            Done(true, $"\"{gamePath}\" has its normal sound again.");
        });

    private Task ApplySoundsModAsync() => framework.RunOnFrameworkThread(() =>
    {
        var ec = new ReloadMod(pi).Invoke(SoundsMod, string.Empty);
        if (ec != PenumbraApiEc.Success) throw new InvalidOperationException($"Penumbra couldn't load \"{SoundsMod}\" ({ec}).");
        var (_, _, collection) = new GetCollectionForObject(pi).Invoke(0);
        new TrySetModPriority(pi).Invoke(collection.Id, SoundsMod, SoundsModPriority);
        new TrySetMod(pi).Invoke(collection.Id, SoundsMod, true);
        new RedrawObject(pi).Invoke(0);
    });

    /// <summary>Creates the "Tundy Sounds" mod once. Framework thread.</summary>
    private string EnsureSoundsMod()
    {
        var dir = Path.Combine(Root, SoundsMod);
        Directory.CreateDirectory(dir);
        var meta = Path.Combine(dir, "meta.json");
        if (!File.Exists(meta))
        {
            File.WriteAllText(meta, JsonSerializer.Serialize(new
            {
                FileVersion = 3,
                Name = SoundsMod,
                Author = "Tundy",
                Description = "Managed by the Tundy plugin's sound replacer: game sounds you replaced with songs. Change it in /tundy → Import.",
                Image = "",
                Version = "1.0.0",
                Website = "",
                ModTags = new[] { "Tundy" },
                DefaultPreferredItems = Array.Empty<object>(),
            }, WriteOptions));
            SongSharer.WriteDefaultMod(dir, new Dictionary<string, string>());
        }
        if (!new GetModList(pi).Invoke().ContainsKey(SoundsMod))
        {
            var ec = new AddMod(pi).Invoke(SoundsMod);
            if (ec != PenumbraApiEc.Success) throw new InvalidOperationException($"Penumbra couldn't add \"{SoundsMod}\" ({ec}).");
        }
        return dir;
    }

    // ------------------------------------------------------------------ shared steps

    private void Run(Func<CancellationToken, Task> work)
    {
        if (!busy.Wait(0))
        {
            Done(false, "Tundy is still working on the last one.");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                StatusOk = true;
                await work(default);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Import failed");
                Done(false, ex.Message);
            }
            finally
            {
                busy.Release();
            }
        });
    }

    private void Done(bool ok, string message)
    {
        StatusOk = ok;
        Status = message;
        Plugin.Log.Information($"Import: {message}");
    }

    /// <returns>The audio file, its title, and whether it's a temporary download to delete afterwards.</returns>
    private async Task<(string File, string Title, bool Temp)> GetAudioAsync(ImportSource source, CancellationToken ct)
    {
        var value = source.Value.Trim().Trim('"');
        if (!source.YouTube)
        {
            if (!File.Exists(value)) throw new FileNotFoundException($"\"{value}\" doesn't exist.");
            return (value, Path.GetFileNameWithoutExtension(value), false);
        }

        if (!Plugin.YouTubeRegex().IsMatch(value)) throw new InvalidOperationException("That isn't a YouTube video link.");
        Status = "Downloading from YouTube...";
        var dir = Path.Combine(Path.GetTempPath(), "tundy-import");
        var song = await ytDlp.DownloadAudioAsync(value, dir, 4 * 3600, ct);
        try { Mp4Fix.StripEditLists(song.File); } catch { /* ffmpeg reads it either way */ }
        return (song.File, song.DisplayTitle, true);
    }

    /// <summary>Converts the audio to match the template's kind of sound: Vorbis music, or mono ADPCM effects.</summary>
    private byte[] Convert(byte[] template, string audio, CancellationToken ct)
    {
        var tool = ffmpeg.Get(ct);
        var filter = AudioPresets.Filter(config.ImportVolume, config.ImportEqPreset);
        if (TemplateIsVorbis(template))
            return VorbisScd.Build(template, VorbisScd.EncodeOgg(tool, audio, 6, ct, filter, silentTail: false));

        var wav = VorbisScd.EncodeWav(tool, audio, filter, ct);
        try { return ScdBuilder.Build(template, ScdBuilder.EncodeAdpcmMono(wav, 44100, ct)); }
        finally { TryDelete(wav); }
    }

    private static bool TemplateIsVorbis(byte[] scd)
    {
        var audioTable = BitConverter.ToInt32(scd, 0x3C);
        var first = BitConverter.ToInt32(scd, audioTable);
        return BitConverter.ToInt32(scd, first + 12) == 0x06;
    }

    /// <summary>One of the mod's own files for <paramref name="route"/>, else the game's own file.</summary>
    private byte[] TemplateFor(string modDir, string route)
    {
        foreach (var (gamePath, file) in MegaMusicMod.ModFiles(modDir))
        {
            if (!gamePath.Equals(route, StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.Combine(modDir, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full)) return File.ReadAllBytes(full);
        }
        return data.GetFile(route)?.Data
               ?? throw new FileNotFoundException($"No sound file for \"{route}\" in the mod or the game.");
    }

    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim();
        if (clean.Length > 60) clean = clean[..60];
        return (clean.Length > 0 ? clean : "song") + "_" + Guid.NewGuid().ToString("N")[..6];
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch { /* ignored */ }
    }

    /// <summary>Copies the mod's option files to "tundy-backup" the first time Tundy changes them.</summary>
    private static void Backup(string modDir)
    {
        var backup = Path.Combine(modDir, "tundy-backup");
        if (Directory.Exists(backup)) return;
        Directory.CreateDirectory(backup);
        foreach (var f in Directory.GetFiles(modDir, "*.json"))
            File.Copy(f, Path.Combine(backup, Path.GetFileName(f)), true);
    }

    // ------------------------------------------------------------------ Penumbra mod files

    /// <summary>The mod's option groups: from meta.json (newer mods) or group_*.json files (older ones).</summary>
    private static List<(JsonObject Group, string File)> Groups(string modDir)
    {
        var result = new List<(JsonObject, string)>();
        var meta = Path.Combine(modDir, "meta.json");
        try
        {
            if (File.Exists(meta) && JsonNode.Parse(File.ReadAllText(meta)) is JsonObject m && m["Groups"] is JsonArray groups)
                result.AddRange(groups.OfType<JsonObject>().Select(g => (g, meta)));
            foreach (var f in Directory.GetFiles(modDir, "group_*.json"))
                if (JsonNode.Parse(File.ReadAllText(f)) is JsonObject g)
                    result.Add((g, f));
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't read the options of {modDir}"); }
        return result;
    }

    private static int FileVersion(string modDir)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(modDir, "meta.json")))?["FileVersion"]?.GetValue<int>() ?? 3;
        }
        catch { return 3; }
    }

    private static void AddOption(string modDir, string name, Dictionary<string, string> files, string description) =>
        EditGroup(modDir, options =>
        {
            foreach (var old in options.OfType<JsonObject>().Where(o => o["Name"]?.GetValue<string>() == name).ToList())
                options.Remove(old);
            var filesNode = new JsonObject();
            foreach (var (gamePath, rel) in files) filesNode[gamePath] = rel;
            var option = new JsonObject { ["Name"] = name, ["Description"] = description, ["Files"] = filesNode };
            if (FileVersion(modDir) >= 4) option["Id"] = Guid.NewGuid().ToString();
            else
            {
                option["FileSwaps"] = new JsonObject();
                option["Manipulations"] = new JsonArray();
            }
            options.Add(option);
        }, create: true);

    /// <summary>Changes the options of the mod's "Tundy imports" group (creating the group first if asked).</summary>
    private static void EditGroup(string modDir, Action<JsonArray> edit, bool create)
    {
        var metaFile = Path.Combine(modDir, "meta.json");
        if (FileVersion(modDir) >= 4)
        {
            var meta = JsonNode.Parse(File.ReadAllText(metaFile)) as JsonObject ?? throw new InvalidDataException("The mod's meta.json can't be read.");
            if (meta["Groups"] is not JsonArray groups) meta["Groups"] = groups = new JsonArray();
            var group = groups.OfType<JsonObject>().FirstOrDefault(g => g["Name"]?.GetValue<string>() == GroupName);
            if (group == null)
            {
                if (!create) return;
                group = new JsonObject
                {
                    ["Type"] = "Single",
                    ["Id"] = Guid.NewGuid().ToString(),
                    ["Name"] = GroupName,
                    ["Description"] = "Songs added with the Tundy plugin. Pick one, or Off.",
                    ["Priority"] = 100,
                    ["Options"] = new JsonArray { new JsonObject { ["Id"] = Guid.NewGuid().ToString(), ["Name"] = "Off" } },
                };
                groups.Add(group);
            }
            if (group["Options"] is not JsonArray options) group["Options"] = options = new JsonArray();
            edit(options);
            File.WriteAllText(metaFile, meta.ToJsonString(WriteOptions));
            return;
        }

        // Older mods: one group_NNN_name.json file per group.
        var existing = Directory.GetFiles(modDir, "group_*.json")
            .FirstOrDefault(f => (JsonNode.Parse(File.ReadAllText(f)) as JsonObject)?["Name"]?.GetValue<string>() == GroupName);
        JsonObject groupFile;
        if (existing != null)
        {
            groupFile = (JsonObject)JsonNode.Parse(File.ReadAllText(existing))!;
        }
        else
        {
            if (!create) return;
            existing = Path.Combine(modDir, $"group_{Directory.GetFiles(modDir, "group_*.json").Length + 1:000}_tundy imports.json");
            groupFile = new JsonObject
            {
                ["Name"] = GroupName,
                ["Description"] = "Songs added with the Tundy plugin. Pick one, or Off.",
                ["Image"] = "",
                ["Page"] = 0,
                ["Priority"] = 100,
                ["Type"] = "Single",
                ["DefaultSettings"] = 0,
                ["Options"] = new JsonArray
                {
                    new JsonObject { ["Name"] = "Off", ["Description"] = "", ["Files"] = new JsonObject(), ["FileSwaps"] = new JsonObject(), ["Manipulations"] = new JsonArray() },
                },
            };
        }
        if (groupFile["Options"] is not JsonArray opts) groupFile["Options"] = opts = new JsonArray();
        edit(opts);
        File.WriteAllText(existing, groupFile.ToJsonString(WriteOptions));
    }

    /// <summary>The mod's always-on file redirects (game path -> file in the mod).</summary>
    private static Dictionary<string, string> ReadDefaultFiles(string modDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            JsonObject? files = null;
            if (FileVersion(modDir) >= 4)
                files = JsonNode.Parse(File.ReadAllText(Path.Combine(modDir, "meta.json")))?["DefaultData"]?["Files"] as JsonObject;
            else if (File.Exists(Path.Combine(modDir, "default_mod.json")))
                files = JsonNode.Parse(File.ReadAllText(Path.Combine(modDir, "default_mod.json")))?["Files"] as JsonObject;
            if (files != null)
                foreach (var (k, v) in files)
                    if (v?.GetValue<string>() is { } s) result[k] = s;
        }
        catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't read the files of {modDir}"); }
        return result;
    }

    // ------------------------------------------------------------------ maintenance

    /// <summary>Problems with songs imported by Tundy, across all your mods (missing files after a rename or move).</summary>
    public List<string> CheckImports()
    {
        var problems = new List<string>();
        Dictionary<string, string> mods;
        try { mods = new GetModList(pi).Invoke(); }
        catch { return new List<string> { "Penumbra isn't available." }; }

        foreach (var mod in mods.Keys)
        {
            if (ModFolder(mod) is not { } dir) continue;
            foreach (var song in Imported(mod))
                if (song.File.Length > 0 && !File.Exists(Path.Combine(dir, song.File)))
                    problems.Add($"\"{mod}\": \"{song.Name}\" is missing its file ({song.File}).");
        }
        if (!mods.ContainsKey(config.ImportTargetMod))
            problems.Add($"The import target \"{config.ImportTargetMod}\" isn't in Penumbra any more (renamed or removed?). Pick it again in the Import tab.");
        if (!mods.ContainsKey(config.QueueTargetMod))
            problems.Add($"The music mod \"{config.QueueTargetMod}\" isn't in Penumbra any more (renamed or removed?). Pick it again in the Sharing tab.");
        return problems;
    }

    /// <summary>Removes imported songs whose files are gone. Returns how many.</summary>
    public int RemoveBrokenImports()
    {
        var removed = 0;
        foreach (var mod in new GetModList(pi).Invoke().Keys)
        {
            if (ModFolder(mod) is not { } dir) continue;
            var broken = Imported(mod).Where(s => s.File.Length > 0 && !File.Exists(Path.Combine(dir, s.File))).Select(s => s.Name).ToHashSet();
            if (broken.Count == 0) continue;
            EditGroup(dir, options =>
            {
                foreach (var o in options.OfType<JsonObject>().Where(o => broken.Contains(o["Name"]?.GetValue<string>() ?? "")).ToList())
                    options.Remove(o);
            }, create: false);
            removed += broken.Count;
            framework.RunOnFrameworkThread(() => new ReloadMod(pi).Invoke(mod, string.Empty));
        }
        return removed;
    }
}
