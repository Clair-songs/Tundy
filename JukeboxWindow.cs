using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Tundy;

/// <summary>
/// The jukebox terminal: link to another player's Tundy, make and edit playlists (saved on your PC) and send them
/// to their queue. Drawn as a terminal in the look picked in Settings → Look (pastel, minimal or Dalamud's).
/// </summary>
public sealed class JukeboxWindow : Window
{
    private readonly Plugin plugin;
    private int selected;
    private string addInput = string.Empty;
    private string renameInput = string.Empty;
    private int renaming = -1;
    private string searchStatus = string.Empty;
    private IReadOnlyList<YouTubeResult> searchResults = Array.Empty<YouTubeResult>();
    private bool searching;
    private readonly SemaphoreSlim lookups = new(2);

    public JukeboxWindow(Plugin plugin) : base("Tundy jukebox terminal###TundyJukebox")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 420), MaximumSize = new Vector2(1600, 1400) };
        Size = new Vector2(720, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        plugin.Jukebox.QueueReceived += SaveTheirQueue;
    }

    private Configuration Cfg => plugin.Config;
    private JukeboxClient Link => plugin.Jukebox;

    /// <summary>Opens the terminal and links it to this player.</summary>
    public void OpenFor(string name, string? world)
    {
        IsOpen = true;
        Link.Connect(name, world);
    }

    public override void PreDraw() => Theme.Push(Cfg.ThemeChoice);

    public override void PostDraw() => Theme.Pop();

    public override void OnClose()
    {
        searchResults = Array.Empty<YouTubeResult>();
    }

    public override void Draw()
    {
        Link.Tick();
        DrawScanlines();

        // Header
        Mono(() => ImGui.TextColored(Theme.Accent, "TUNDY//JUKEBOX_TERMINAL"));
        var (state, color) = Link.State switch
        {
            JukeboxClient.LinkState.Linked => ("LINK ESTABLISHED", Theme.Good),
            JukeboxClient.LinkState.Connecting => ("LINKING...", Theme.Warn),
            JukeboxClient.LinkState.Off => ("THEIR JUKEBOX IS OFF", Theme.Warn),
            JukeboxClient.LinkState.Failed => ("LINK FAILED", Theme.Bad),
            _ => ("NO TARGET", Theme.Muted),
        };
        var extra = Link.State == JukeboxClient.LinkState.Linked && Link.Info != null
            ? $" · v{Link.Info.Version}" + (Link.Ping is { } ping ? $" · {ping.TotalSeconds:0.0} s" : "")
            : "";
        var status = "■ " + state + extra;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - ImGui.CalcTextSize(status).X);
        Mono(() => ImGui.TextColored(color, status));

        DrawTarget();
        ImGui.Spacing();

        // Playlists | songs
        var height = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() * 2 - ImGui.GetTextLineHeightWithSpacing() - 8;
        ImGui.BeginChild("lists", new Vector2(190 * ImGuiHelpers.GlobalScale, height), true);
        DrawPlaylists();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("songs", new Vector2(0, height), true);
        DrawSongs();
        ImGui.EndChild();

        DrawSendBar();
    }

    // ---- look

    private void Mono(Action draw)
    {
        using (plugin.MonoFont.Push()) draw();
    }

    /// <summary>Faint horizontal lines behind everything, for the terminal look.</summary>
    private static void DrawScanlines()
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var color = ImGui.GetColorU32(Theme.TextLight with { W = 0.025f });
        for (var y = min.Y; y < max.Y; y += 3 * ImGuiHelpers.GlobalScale) dl.AddLine(new Vector2(min.X, y), new Vector2(max.X, y), color);
    }

    private static void Label(string text) => ImGui.TextColored(Theme.Heading, text);

    // ---- target

    private void DrawTarget()
    {
        Ui.BeginCard();
        if (Link.TargetName == null)
        {
            Label("TARGET");
            ImGui.TextColored(Theme.Muted, "No player linked. Right-click a player and pick \"Tundy: open jukebox terminal\", "
                                           + "or target them and type /tundy jukebox. Your playlists below work either way.");
            Ui.EndCard();
            return;
        }

        if (ImGui.BeginTable("target", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            Label("TARGET");
            Mono(() => ImGui.TextUnformatted(Link.TargetName.ToUpperInvariant()));
            if (Link.TargetWorld != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(Theme.Muted, "@ " + Link.TargetWorld.ToUpperInvariant());
            }
            ImGui.TextColored(Theme.Muted, Link.State switch
            {
                JukeboxClient.LinkState.Connecting => "Asking their Tundy...",
                JukeboxClient.LinkState.Failed => "No answer. They need Tundy with the jukebox turned on (and to be able to get your tells).",
                JukeboxClient.LinkState.Off => "They have Tundy, but their jukebox is turned off.",
                _ when Link.Info is { } i => string.Join(" · ", new[]
                {
                    i.Accepting ? null : "not taking songs right now",
                    i.NeedsApproval ? "they approve each send" : "songs go straight in",
                    i.CooldownSeconds > 0 ? $"you can send more in {Math.Ceiling(i.CooldownSeconds / 60.0)} min" : null,
                    $"up to {i.MaxSongs} songs per send",
                }.Where(s => s != null)),
                _ => "",
            });

            ImGui.TableNextColumn();
            Label("NOW_PLAYING");
            if (Link.Info is { } info)
            {
                ImGui.TextUnformatted(info.NowPlaying ?? "nothing");
                ImGui.TextColored(Theme.Muted, $"queue: {info.Queued} song{(info.Queued == 1 ? "" : "s")}"
                                               + (info.QueuedSeconds > 0 ? $" · {Ui.About(info.QueuedSeconds)}" : ""));
            }
            else ImGui.TextColored(Theme.Muted, "-");
            if (ImGui.SmallButton("Refresh")) Link.Refresh();
            ImGui.SameLine();
            if (ImGui.SmallButton("Disconnect")) Link.Disconnect();
            ImGui.EndTable();
        }
        Ui.EndCard();
    }

    // ---- playlists

    private List<JukeboxPlaylist> Playlists => Cfg.JukeboxPlaylists;

    private JukeboxPlaylist? Current => selected >= 0 && selected < Playlists.Count ? Playlists[selected] : null;

    private void DrawPlaylists()
    {
        Label("YOUR_PLAYLISTS");
        for (var i = 0; i < Playlists.Count; i++)
        {
            var p = Playlists[i];
            ImGui.PushID(i);
            if (renaming == i)
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText("##rename", ref renameInput, 40, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
                {
                    if (renameInput.Trim().Length > 0) p.Name = renameInput.Trim();
                    renaming = -1;
                    plugin.SaveConfig();
                }
                if (!ImGui.IsItemActive() && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.IsItemHovered()) renaming = -1;
            }
            else
            {
                if (ImGui.Selectable($"{p.Name}##pl", selected == i)) selected = i;
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { renaming = i; renameInput = p.Name; }
                ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(p.Songs.Count.ToString()).X);
                ImGui.TextColored(Theme.Muted, p.Songs.Count.ToString());
                if (ImGui.BeginPopupContextItem("plmenu"))
                {
                    if (ImGui.MenuItem("Rename")) { renaming = i; renameInput = p.Name; }
                    if (ImGui.MenuItem("Duplicate"))
                    {
                        Playlists.Insert(i + 1, new JukeboxPlaylist { Name = p.Name + " (copy)", Songs = p.Songs.Select(s => new JukeboxSong { Url = s.Url, Title = s.Title, Seconds = s.Seconds }).ToList() });
                        plugin.SaveConfig();
                    }
                    if (ImGui.MenuItem("Delete (hold Ctrl)") && ImGui.GetIO().KeyCtrl)
                    {
                        Playlists.RemoveAt(i);
                        selected = Math.Min(selected, Playlists.Count - 1);
                        plugin.SaveConfig();
                        ImGui.EndPopup();
                        ImGui.PopID();
                        break;
                    }
                    ImGui.EndPopup();
                }
            }
            ImGui.PopID();
        }

        ImGui.Spacing();
        if (ImGui.Selectable("+ New playlist"))
        {
            Playlists.Add(new JukeboxPlaylist { Name = $"Playlist {Playlists.Count + 1}" });
            selected = Playlists.Count - 1;
            renaming = selected;
            renameInput = Playlists[selected].Name;
            plugin.SaveConfig();
        }
        ImGui.Spacing();
        ImGui.TextColored(Theme.Muted, "Saved on your PC. Double-click to rename, right-click for more.");
    }

    // ---- songs

    private void DrawSongs()
    {
        if (Current is not { } p)
        {
            Label("NO_PLAYLIST");
            ImGui.TextColored(Theme.Muted, "Make a playlist on the left, then add songs here.");
            return;
        }

        var total = p.Songs.Sum(s => s.Seconds ?? 0);
        Label($"{p.Name.ToUpperInvariant()} // {p.Songs.Count} TRACK{(p.Songs.Count == 1 ? "" : "S")}" + (total > 0 ? $" · {Ui.Length(total)}" : ""));

        // Add: a link, or a YouTube search
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70 * ImGuiHelpers.GlobalScale);
        var go = ImGui.InputTextWithHint("##add", "> paste a YouTube or Spotify link, or search YouTube", ref addInput, 300, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        go |= ImGui.Button(LooksLikeLink(addInput) ? "+ Add" : "Search");
        if (go && addInput.Trim().Length > 0) AddOrSearch(p);
        if (searchStatus.Length > 0) ImGui.TextColored(Theme.Muted, searchStatus);
        if (searchResults.Count > 0) DrawSearchResults(p);

        ImGui.Separator();
        var rowH = ImGui.GetFrameHeight();
        for (var i = 0; i < p.Songs.Count; i++)
        {
            var s = p.Songs[i];
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"{i + 1:00}");
            ImGui.SameLine();
            var right = (rowH + ImGui.GetStyle().ItemSpacing.X) * 3 + 50 * ImGuiHelpers.GlobalScale;
            ImGui.TextUnformatted(Ui.Ellipsize(s.Title ?? s.Url, ImGui.GetContentRegionAvail().X - right));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.Url);
            ImGui.SameLine(ImGui.GetContentRegionMax().X - right + 6 * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(Theme.Muted, Ui.Length(s.Seconds));
            ImGui.SameLine(ImGui.GetContentRegionMax().X - (rowH + ImGui.GetStyle().ItemSpacing.X) * 3);
            if (Ui.IconButton("up", FontAwesomeIcon.ArrowUp, "Move up", i == 0)) { (p.Songs[i - 1], p.Songs[i]) = (p.Songs[i], p.Songs[i - 1]); plugin.SaveConfig(); }
            ImGui.SameLine();
            if (Ui.IconButton("down", FontAwesomeIcon.ArrowDown, "Move down", i == p.Songs.Count - 1)) { (p.Songs[i + 1], p.Songs[i]) = (p.Songs[i], p.Songs[i + 1]); plugin.SaveConfig(); }
            ImGui.SameLine();
            if (Ui.IconButton("rm", FontAwesomeIcon.Times, "Remove from this playlist"))
            {
                p.Songs.RemoveAt(i);
                plugin.SaveConfig();
                ImGui.PopID();
                break;
            }
            ImGui.PopID();
        }
        if (p.Songs.Count == 0) ImGui.TextColored(Theme.Muted, "No songs yet: paste a link above, or type a few words to search YouTube.");
    }

    private static bool LooksLikeLink(string s)
    {
        s = s.Trim();
        return Plugin.YouTubeRegex().IsMatch(s) || Plugin.SpotifyRegex().IsMatch(s);
    }

    private void AddOrSearch(JukeboxPlaylist p)
    {
        var text = addInput.Trim();
        if (LooksLikeLink(text))
        {
            AddSong(p, text, null, null);
            addInput = string.Empty;
            searchResults = Array.Empty<YouTubeResult>();
            searchStatus = string.Empty;
            return;
        }
        if (searching) return;
        searching = true;
        searchStatus = $"Searching YouTube for \"{text}\"...";
        _ = Task.Run(async () =>
        {
            try
            {
                searchResults = await plugin.YtDlp.SearchAsync(text, 6, default);
                searchStatus = searchResults.Count == 0 ? "Nothing found." : "Pick a result to add it:";
            }
            catch (Exception ex)
            {
                searchStatus = $"Search failed: {RetryPolicy.Short(ex.Message)}";
            }
            finally { searching = false; }
        });
    }

    private void DrawSearchResults(JukeboxPlaylist p)
    {
        foreach (var r in searchResults.ToList())
        {
            ImGui.PushID(r.Id);
            if (Ui.IconButton("add", FontAwesomeIcon.Plus, "Add to this playlist"))
            {
                AddSong(p, $"https://www.youtube.com/watch?v={r.Id}", TitleCleaner.Clean(r.Title), r.Seconds);
                searchResults = searchResults.Where(x => x.Id != r.Id).ToList();
            }
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Ui.Ellipsize(TitleCleaner.Clean(r.Title), ImGui.GetContentRegionAvail().X - 60 * ImGuiHelpers.GlobalScale));
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, Ui.Length(r.Seconds));
            ImGui.PopID();
        }
        if (ImGui.SmallButton("Close results")) { searchResults = Array.Empty<YouTubeResult>(); searchStatus = string.Empty; }
    }

    private void AddSong(JukeboxPlaylist p, string url, string? title, int? seconds)
    {
        if (p.Songs.Any(s => JukeboxProtocol.IdFor(s.Url) == JukeboxProtocol.IdFor(url))) { searchStatus = "That song is already in this playlist."; return; }
        var song = new JukeboxSong { Url = url, Title = title, Seconds = seconds };
        p.Songs.Add(song);
        plugin.SaveConfig();
        if (title == null || seconds == null) LookUp(song);
    }

    /// <summary>Finds a song's name and length in the background (at most two at once).</summary>
    private void LookUp(JukeboxSong song)
    {
        _ = Task.Run(async () =>
        {
            await lookups.WaitAsync();
            try
            {
                var spotify = Plugin.SpotifyRegex().Match(song.Url);
                if (spotify.Success)
                {
                    var t = await SpotifyResolver.GetTrackAsync(spotify.Groups["id"].Value, default);
                    song.Title ??= t.Display;
                    song.Seconds ??= t.DurationSeconds;
                }
                else if (await plugin.YtDlp.GetInfoAsync(song.Url, default) is { } info)
                {
                    song.Title ??= info.DisplayTitle;
                    song.Seconds ??= info.Seconds;
                }
                plugin.SaveConfig();
            }
            catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't look up {song.Url}"); }
            finally { lookups.Release(); }
        });
    }

    /// <summary>"Save their queue": their queue's songs become a new playlist.</summary>
    private void SaveTheirQueue(string name, List<string> ids)
    {
        var p = new JukeboxPlaylist { Name = $"{name}'s queue {DateTime.Now:MMM d}" };
        foreach (var id in ids)
            if (JukeboxProtocol.FromId(id) is { } s) p.Songs.Add(new JukeboxSong { Url = s.Url });
        Cfg.JukeboxPlaylists.Add(p);
        selected = Cfg.JukeboxPlaylists.Count - 1;
        plugin.SaveConfig();
        foreach (var song in p.Songs) LookUp(song);
    }

    // ---- send

    private void DrawSendBar()
    {
        var linked = Link.State == JukeboxClient.LinkState.Linked && Link.Info is { Accepting: true };
        var p = Current;
        var canSend = linked && p is { Songs.Count: > 0 };

        if (!canSend) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.PrimaryButton);
        var target = Link.TargetName ?? "their";
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, $"Send to {target}'s queue")) Link.Send(p!.Songs, shuffle: false);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Random, "Shuffle & send")) Link.Send(p!.Songs, shuffle: true);
        if (!canSend) ImGui.EndDisabled();
        if (!canSend && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!linked ? "Link to a player with Tundy first (and they need to be taking songs)." : "Add songs to the playlist first.");

        ImGui.SameLine();
        if (!linked) ImGui.BeginDisabled();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, "Save their queue")) Link.RequestQueue();
        if (!linked) ImGui.EndDisabled();
        Ui.Tooltip("Copies the songs in their queue into a new playlist of yours.");

        ImGui.PushTextWrapPos();
        if (Link.LastResult.Length > 0) ImGui.TextColored(Link.LastResultOk ? Theme.Muted : Theme.Bad, "> " + Link.LastResult);
        else ImGui.TextColored(Theme.Muted, "> Songs go in one hidden tell (or a few, for big playlists). Their rules apply: approval, limits, cooldowns.");
        ImGui.PopTextWrapPos();
    }
}
