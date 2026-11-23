using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Plugin;

namespace Tundy;

/// <summary>
/// Loads Tundy's settings. The first time, it copies them from TellTunes (all of them) and from Soundy
/// (SoundyQ's queue setup and the importer's settings), so Tundy starts out set up the way you had things.
/// Each import happens once; TellTunes' and Soundy's own files are only read, never changed.
/// </summary>
internal static class SettingsImporter
{
    public static Configuration LoadOrImport(IDalamudPluginInterface pi, out List<string> notes)
    {
        notes = new List<string>();
        var existing = pi.GetPluginConfig() as Configuration;
        var config = existing ?? new Configuration();
        var configs = Directory.GetParent(pi.GetPluginConfigDirectory())!.FullName;
        var changed = existing == null;

        if (!config.ImportedTellTunes)
        {
            // Only into a brand-new Tundy setup, so it never overwrites settings you changed in Tundy.
            if (existing == null && FromTellTunes(Path.Combine(configs, "TellTunes.json")) is { } imported)
            {
                config = imported;
                notes.Add("Copied your settings from TellTunes.");
            }
            config.ImportedTellTunes = true;
            changed = true;
        }

        if (!config.ImportedSoundy)
        {
            if (FromSoundy(config, Path.Combine(configs, "Soundy.json")))
                notes.Add("Copied your SoundyQ and Soundy import settings from Soundy.");
            config.ImportedSoundy = true;
            changed = true;
        }

        // A brand-new Tundy setup: the welcome guide opens with /tundy, and requests wait until it's finished
        // (they're turned on in its last step, or by hand), so nobody's song plays before you've chosen who can send them.
        if (existing == null)
        {
            config.ShowWelcome = true;
            config.Enabled = false;
        }

        if (changed) pi.SavePluginConfig(config);
        return config;
    }

    /// <summary>TellTunes' settings use the same names as Tundy's, so they're read straight in.</summary>
    private static Configuration? FromTellTunes(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject node) return null;
            node.Remove("$type"); // "TellTunes.Configuration, TellTunes"
            return node.Deserialize<Configuration>();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't copy TellTunes' settings");
            return null;
        }
    }

    /// <summary>Copies the parts of Soundy's settings Tundy uses. Returns false if there was nothing to read.</summary>
    private static bool FromSoundy(Configuration config, string file)
    {
        try
        {
            if (!File.Exists(file)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            bool Has(string name, JsonValueKind kind, out JsonElement value) =>
                root.TryGetProperty(name, out value) && value.ValueKind == kind;
            bool? Bool(string name) =>
                root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            List<string> Strings(JsonElement arr) => arr.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!.Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Importer
            if (Has("PenumbraPath", JsonValueKind.String, out var path)) config.PenumbraPath = path.GetString() ?? "";
            if (Has("Volume", JsonValueKind.Number, out var vol)) config.ImportVolume = (float)vol.GetDouble();
            if (Has("EqPreset", JsonValueKind.Number, out var eq)) config.ImportEqPreset = eq.GetInt32();
            if (Has("BusChoice", JsonValueKind.Number, out var bus)) config.ImportBusChoice = bus.GetInt32();
            if (Has("PreferredImportSource", JsonValueKind.Number, out var src)) config.ImportSource = src.GetInt32();

            // SoundyQ
            if (Has("SoundyQAliases", JsonValueKind.Array, out var aliases) && Strings(aliases) is { Count: > 0 } a)
                config.QueueAliases = a.Where(x => !x.Contains(' ')).ToList();
            if (Has("SoundyQDefaultAccessMode", JsonValueKind.Number, out var access) && Enum.IsDefined(typeof(AccessMode), access.GetInt32()))
                config.QueueAccessMode = (AccessMode)access.GetInt32();
            if (Has("SoundyQTargetMod", JsonValueKind.String, out var mod) && !string.IsNullOrWhiteSpace(mod.GetString()))
                config.QueueTargetMod = mod.GetString()!;
            if (Has("SoundyQTargetScdRoutes", JsonValueKind.Array, out var routes) && Strings(routes) is { Count: > 0 } r)
                config.QueueScdRoutes = r;
            if (Has("SoundyQEmoteCommand", JsonValueKind.String, out var emote) && !string.IsNullOrWhiteSpace(emote.GetString()))
                config.QueueEmoteCommand = emote.GetString()!.Trim();
            if (Bool("SoundyQCleanupAfterPlay") is { } cleanup) config.DeleteAfterPlay = cleanup;

            if (Has("SoundyQWhitelistEntries", JsonValueKind.Array, out var entries))
            {
                config.QueuePlayerRules = new List<QueuePlayerRule>();
                foreach (var e in entries.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                {
                    string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? "" : "";
                    var player = S("PlayerName");
                    var world = S("World");
                    if (player.Length == 0) continue;

                    // Soundy sometimes stored a name twice, split in the wrong place ("Alex ExampleAlex" @ "Example").
                    // CleanName repairs that; the "world" it stored is then really part of the name.
                    var name = Names.Clean($"{player} @ {world}");
                    var repaired = !name.Equals(player, StringComparison.OrdinalIgnoreCase);
                    if (config.QueuePlayerRules.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                    config.QueuePlayerRules.Add(new QueuePlayerRule
                    {
                        Name = name,
                        World = repaired ? "" : world,
                        Permission = e.TryGetProperty("Permission", out var p) && p.ValueKind == JsonValueKind.Number
                                     && Enum.IsDefined(typeof(RequestPermission), p.GetInt32())
                            ? (RequestPermission)p.GetInt32()
                            : RequestPermission.PreQueue,
                        Enabled = !e.TryGetProperty("Enabled", out var en) || en.ValueKind != JsonValueKind.False,
                    });
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Couldn't copy Soundy's settings");
            return false;
        }
    }
}
