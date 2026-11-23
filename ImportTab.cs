using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;

namespace Tundy;

/// <summary>The "Import" tab: Soundy's import tools (songs into a mod's playlist, game sound replacer).</summary>
public sealed class ImportTab
{
    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();

    private int sourceKind; // 0 = YouTube, 1 = file on your PC
    private string youtubeLink = string.Empty;
    private string localFile = string.Empty;
    private string songName = string.Empty;
    private List<(string Route, int Uses)>? routes;
    private string routesFor = string.Empty;
    private string soundPath = string.Empty;
    private List<ImportedSong>? imported;
    private DateTime importedAt = DateTime.MinValue;
    private string importedFor = string.Empty;
    private Dictionary<string, string> replaced = new();
    private DateTime replacedAt = DateTime.MinValue;

    public ImportTab(Plugin plugin) => this.plugin = plugin;

    private ImportSource Source => sourceKind == 0 ? new ImportSource(true, youtubeLink) : new ImportSource(false, localFile);

    public void Draw()
    {
        var cfg = plugin.Config;
        var importer = plugin.Importer;
        var changed = false;

        ImGui.BeginChild("import", Vector2.Zero, false);
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled("Add songs to your mods for good (Soundy's import tools). Requests sent by tell don't need this.");
        ImGui.PopTextWrapPos();

        // ---- Source
        Ui.Section("Song");
        ImGui.RadioButton("YouTube link", ref sourceKind, 0);
        ImGui.SameLine();
        ImGui.RadioButton("File on your PC", ref sourceKind, 1);
        if (sourceKind == 0)
        {
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##yt", "https://www.youtube.com/watch?v=...", ref youtubeLink, 512);
        }
        else
        {
            var browseW = ImGui.CalcTextSize("Browse...").X + ImGui.GetStyle().FramePadding.X * 2;
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - browseW - ImGui.GetStyle().ItemSpacing.X);
            ImGui.InputTextWithHint("##file", "C:\\Music\\song.mp3", ref localFile, 512);
            ImGui.SameLine();
            if (ImGui.Button("Browse..."))
                fileDialog.OpenFileDialog("Pick a song", "Audio{.mp3,.m4a,.ogg,.opus,.wav,.flac,.aac,.webm,.mp4}",
                    (ok, path) => { if (ok) localFile = path; });
        }
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("Name", "Leave empty to use the song's title", ref songName, 80);

        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var loud = cfg.ImportVolume;
        if (ImGui.SliderFloat("Loudness", ref loud, 1f, 5f, "%.1f")) { cfg.ImportVolume = loud; changed = true; }
        Ui.Help("1.0 = the song as it is. Higher makes it louder; a limiter keeps it from distorting.");
        ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
        var eq = Math.Clamp(cfg.ImportEqPreset, 0, AudioPresets.Names.Length - 1);
        if (ImGui.Combo("EQ", ref eq, AudioPresets.Names, AudioPresets.Names.Length)) { cfg.ImportEqPreset = eq; changed = true; }
        Ui.Help("Tone presets from Soundy. Neutral leaves the sound as it is.");

        // ---- Into a mod
        Ui.Section("Add it to a mod");
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("Mod", cfg.ImportTargetMod))
        {
            foreach (var (folder, name) in plugin.Music.ModList().OrderBy(m => m.Value, StringComparer.OrdinalIgnoreCase))
                if (ImGui.Selectable(name == folder ? name : $"{name} ({folder})", folder == cfg.ImportTargetMod))
                {
                    cfg.ImportTargetMod = folder;
                    routes = null;
                    imported = null;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        Ui.Help("The mod the song is added to, as a new option in its \"Tundy imports\" group. For the Mega Music Mod "
                + "it becomes a song for the DJ dance.");

        if (routes == null || routesFor != cfg.ImportTargetMod)
        {
            routesFor = cfg.ImportTargetMod;
            routes = importer.ModFolder(cfg.ImportTargetMod) is { } dir ? MegaMusicMod.ScanRoutes(dir) : new();
        }
        ImGui.TextUnformatted("Sound file(s) it plays on:");
        Ui.Help("The game sound files this mod puts its sounds on, most used first. The top one is usually the main one "
                + "(sound/lolo.scd for the Mega Music Mod's DJ dance). Animations in the mod play these files.");
        if (routes.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudOrange, "This mod doesn't put sounds on any sound file, so there's nothing to replace in it. Use the sound replacer below instead.");
        else
        {
            ImGui.BeginChild("routes", new Vector2(0, Math.Min(routes.Count, 5) * ImGui.GetFrameHeightWithSpacing() + 4), true);
            foreach (var (route, uses) in routes)
            {
                var on = cfg.ImportRoutes.Contains(route, StringComparer.OrdinalIgnoreCase);
                if (ImGui.Checkbox($"{route}  ({uses} option{(uses == 1 ? "" : "s")})##imp{route}", ref on))
                {
                    if (on) cfg.ImportRoutes.Add(route);
                    else cfg.ImportRoutes.RemoveAll(r => r.Equals(route, StringComparison.OrdinalIgnoreCase));
                    changed = true;
                }
            }
            ImGui.EndChild();
        }
        var chosen = cfg.ImportRoutes.Where(r => routes.Any(x => x.Route.Equals(r, StringComparison.OrdinalIgnoreCase))).ToList();

        var select = cfg.ImportSelectAfter;
        if (ImGui.Checkbox("Pick it in the mod right away", ref select)) { cfg.ImportSelectAfter = select; changed = true; }
        if (cfg.ImportTargetMod == cfg.QueueTargetMod && cfg.BetweenSongsSilence)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(ImGuiColors.DalamudOrange,
                "Tundy keeps this mod's song slot silent between requests, so you won't hear picked songs while Tundy is on. "
                + "Change \"Between requests\" in the Sharing tab to hear them.");
            ImGui.PopTextWrapPos();
        }

        var ready = !importer.Busy && chosen.Count > 0 && (sourceKind == 0 ? youtubeLink.Trim().Length > 0 : localFile.Trim().Length > 0);
        if (!ready) ImGui.BeginDisabled();
        if (ImGui.Button("Import"))
        {
            importer.Import(Source, songName, cfg.ImportTargetMod, chosen, cfg.ImportSelectAfter);
            imported = null;
        }
        if (!ready) ImGui.EndDisabled();
        ImGui.SameLine();
        DrawStatus();

        // Songs already added
        if (imported == null || importedFor != cfg.ImportTargetMod || (DateTime.Now - importedAt).TotalSeconds > 3)
        {
            importedFor = cfg.ImportTargetMod;
            importedAt = DateTime.Now;
            imported = importer.Imported(cfg.ImportTargetMod);
        }
        if (imported.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Songs added with Tundy ({imported.Count}):");
            foreach (var song in imported)
            {
                ImGui.PushID("song" + song.Name);
                if (Ui.IconButton("use", FontAwesomeIcon.Play, "Pick this song in the mod")) importer.Use(cfg.ImportTargetMod, song.Name);
                ImGui.SameLine();
                if (Ui.IconButton("rm", FontAwesomeIcon.Trash, "Remove it from the mod (hold Ctrl)") && ImGui.GetIO().KeyCtrl)
                {
                    importer.Remove(cfg.ImportTargetMod, song.Name);
                    imported = null;
                }
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(song.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{song.Description}\n{song.File}");
                ImGui.PopID();
                if (imported == null) break;
            }
            if (ImGui.SmallButton("Turn imports off")) importer.Use(cfg.ImportTargetMod, "Off");
            Ui.Tooltip("Picks \"Off\" in the mod's Tundy imports group, so its own songs play again.");
        }

        // ---- Sound replacer
        Ui.Section("Sound replacer");
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled("Puts the song above on any game sound, e.g. an emote's sound, through Tundy's own \"Tundy Sounds\" mod. "
                           + "Your Lightless pairs get it like any mod.");
        ImGui.PopTextWrapPos();
        ImGui.SetNextItemWidth(320 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##sound", "sound/vfx/etc/se_vfx_etc_emot_idea.scd", ref soundPath, 200);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##emotes", "Emote sounds", ImGuiComboFlags.None))
        {
            foreach (var e in SongSharer.Emotes.DistinctBy(e => e.ScdPath))
                if (ImGui.Selectable($"{e.Name} ({e.Command})")) soundPath = e.ScdPath;
            ImGui.EndCombo();
        }
        var canReplace = !importer.Busy && soundPath.Trim().Length > 0 && (sourceKind == 0 ? youtubeLink.Trim().Length > 0 : localFile.Trim().Length > 0);
        if (!canReplace) ImGui.BeginDisabled();
        if (ImGui.Button("Replace this sound")) importer.ReplaceSound(Source, soundPath);
        if (!canReplace) ImGui.EndDisabled();

        if ((DateTime.Now - replacedAt).TotalSeconds > 2) { replaced = importer.ReplacedSounds(); replacedAt = DateTime.Now; }
        foreach (var (gamePath, _) in replaced)
        {
            ImGui.PushID("rep" + gamePath);
            if (Ui.IconButton("undo", FontAwesomeIcon.Undo, "Give it its normal sound back")) importer.RestoreSound(gamePath);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(gamePath);
            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.PushTextWrapPos();
        ImGui.TextDisabled("Not included from Soundy: adding a sound to an animation that has none, and editing visual effects. "
                           + "Both need VFXEditor's file tools; songs here go on sound files that animations already play.");
        ImGui.PopTextWrapPos();

        ImGui.EndChild();
        fileDialog.Draw();
        if (changed) plugin.SaveConfig();
    }

    private void DrawStatus()
    {
        var importer = plugin.Importer;
        if (importer.Status.Length == 0) return;
        ImGui.PushTextWrapPos();
        ImGui.TextColored(importer.Busy ? ImGuiColors.ParsedBlue : importer.StatusOk ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed,
            importer.Busy && plugin.Ffmpeg.Downloading ? plugin.Ffmpeg.Status : importer.Status);
        ImGui.PopTextWrapPos();
    }
}
