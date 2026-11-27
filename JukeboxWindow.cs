using System;
using System.Collections.Concurrent;
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
/// The jukebox terminal: link to another player's Tundy, make and edit playlists (saved on your PC), send them or
/// single songs to their queue, and see their queue. Drawn as a terminal in the look picked in Settings → Look.
/// </summary>
public sealed class JukeboxWindow : Window
{
    private readonly Plugin plugin;
    private int selected;
    private string addInput = string.Empty;
    private string renameInput = string.Empty;
    private int renaming = -1;
    private int deleting = -1;
    private string searchStatus = string.Empty;
    private IReadOnlyList<YouTubeResult> searchResults = Array.Empty<YouTubeResult>();
    private bool searching;
    private readonly SemaphoreSlim lookups = new(2);

    // Names and lengths of songs in their queue (looked up here: the link only carries ids).
    private readonly ConcurrentDictionary<string, JukeboxSong> known = new();

    public JukeboxWindow(Plugin plugin) : base("Tundy jukebox terminal###TundyJukebox")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(600, 440), MaximumSize = new Vector2(1600, 1400) };
        Size = new Vector2(760, 580);
        SizeCondition = ImGuiCond.FirstUseEver;
        plugin.Jukebox.QueueReceived += SaveTheirQueue;
    }

    private Configuration Cfg => plugin.Config;
    private JukeboxClient Link => plugin.Jukebox;
    private bool Linked => Link.State == JukeboxClient.LinkState.Linked;
    private bool CanSend => Linked && Link.Info is { Accepting: true };

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
        searchStatus = string.Empty;
        Link.Sleep(); // their Tundy stops sending "now playing" updates until the terminal is open again
    }

    public override void Draw()
    {
        while (uiWork.TryDequeue(out var work)) work();
        Link.Tick();
        DrawScanlines();
        DrawHeader();
        DrawTarget();
        ImGui.Spacing();

        if (ImGui.BeginTabBar("jukeboxTabs"))
        {
            if (ImGui.BeginTabItem("Playlists"))
            {
                DrawPlaylistsTab();
                ImGui.EndTabItem();
            }
            var queueLabel = Link.Info is { } i ? $"Their queue ({i.Queued})###theirQueue" : "Their queue###theirQueue";
            if (ImGui.BeginTabItem(queueLabel))
            {
                DrawTheirQueueTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    // ---- look

    private void Mono(Action draw)
    {
        using (plugin.MonoFont.Push()) draw();
    }

    private float MonoWidth(string text)
    {
        using (plugin.MonoFont.Push()) return ImGui.CalcTextSize(text).X;
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

    private static void Muted(string text)
    {
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Theme.Muted, text);
        ImGui.PopTextWrapPos();
    }

    // ---- header and target

    private void DrawHeader()
    {
        const string title = "TUNDY//JUKEBOX_TERMINAL";
        Mono(() => ImGui.TextColored(Theme.Accent, title));
        var (state, color) = Link.State switch
        {
            JukeboxClient.LinkState.Linked => ("LINK ESTABLISHED", Theme.Good),
            JukeboxClient.LinkState.Connecting => ("LINKING...", Theme.Warn),
            JukeboxClient.LinkState.Off => ("THEIR JUKEBOX IS OFF", Theme.Warn),
            JukeboxClient.LinkState.Failed => ("LINK FAILED", Theme.Bad),
            _ => ("NO TARGET", Theme.Muted),
        };
        var extra = Linked && Link.Info != null ? $" · v{Link.Info.Version}" + (Link.Ping is { } ping ? $" · {ping.TotalSeconds:0.0} s" : "") : "";
        var status = state + extra;

        // Measured in the same (monospace) font it's drawn in, so it isn't cut off at the right edge.
        var width = MonoWidth(status);
        var right = ImGui.GetWindowContentRegionMax().X;
        if (ImGui.GetCursorPosX() + MonoWidth(title) + width + 20 * ImGuiHelpers.GlobalScale < right)
            ImGui.SameLine(right - width);
        Mono(() => ImGui.TextColored(color, status));
    }

    private void DrawTarget()
    {
        Ui.BeginCard();
        if (Link.TargetName == null)
        {
            Label("TARGET");
            Muted("No player linked. Right-click a player and pick \"Tundy: open jukebox terminal\", or target them and type "
                  + "/tundy jukebox. Your playlists below work either way.");
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
            Muted(Link.State switch
            {
                JukeboxClient.LinkState.Connecting => "Asking their Tundy...",
                JukeboxClient.LinkState.Failed => "No answer. They need Tundy with the jukebox turned on (and to be able to get your tells).",
                JukeboxClient.LinkState.Off => "They have Tundy, but their jukebox is turned off.",
                _ when Link.Info is { } i => string.Join(" · ", new[]
                {
                    i.Accepting ? null : "not taking songs right now",
                    i.NeedsApproval ? "they approve each send" : "songs go straight in",
                    i.CooldownSeconds > 0 ? $"more playlists in {Math.Ceiling(i.CooldownSeconds / 60.0)} min" : null,
                    $"up to {i.MaxSongs} songs per send",
                }.Where(s => s != null)),
                _ => "",
            });

            ImGui.TableNextColumn();
            Label("NOW_PLAYING");
            if (Link.Info is { } info)
            {
                ImGui.PushTextWrapPos(0);
                ImGui.TextUnformatted(info.NowPlaying ?? "nothing");
                ImGui.PopTextWrapPos();
                Muted($"queue: {info.Queued} song{(info.Queued == 1 ? "" : "s")}"
                      + (info.QueuedSeconds > 0 ? $" · {Ui.About(info.QueuedSeconds)}" : "")
                      + $" · updated {Ui.Ago(Link.InfoAt)}");
            }
            else ImGui.TextColored(Theme.Muted, "-");
            if (ImGui.SmallButton("Refresh")) { Link.Refresh(); if (Linked) Link.RequestQueue(); }
            ImGui.SameLine();
            if (ImGui.SmallButton("Disconnect")) Link.Disconnect();
            ImGui.EndTable();
        }
        Ui.EndCard();
    }

    // ---- playlists tab

    private List<JukeboxPlaylist> Playlists => Cfg.JukeboxPlaylists;

    private JukeboxPlaylist? Current => selected >= 0 && selected < Playlists.Count ? Playlists[selected] : null;

    private void DrawPlaylistsTab()
    {
        var height = ImGui.GetContentRegionAvail().Y - ImGui.GetFrameHeightWithSpacing() - ImGui.GetTextLineHeightWithSpacing() * 2 - 8;
        ImGui.BeginChild("lists", new Vector2(200 * ImGuiHelpers.GlobalScale, height), true);
        DrawPlaylists();
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("songs", new Vector2(0, height), true);
        DrawSongs();
        ImGui.EndChild();
        DrawSendBar();
    }

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
                if (ImGui.IsWindowAppearing() || !ImGui.IsAnyItemActive()) ImGui.SetKeyboardFocusHere();
                if (ImGui.InputText("##rename", ref renameInput, 40, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
                {
                    if (renameInput.Trim().Length > 0) p.Name = renameInput.Trim();
                    renaming = -1;
                    plugin.SaveConfig();
                }
                if (ImGui.IsItemDeactivated()) renaming = -1;
            }
            else
            {
                var count = p.Songs.Count.ToString();
                if (ImGui.Selectable($"{p.Name}##pl", selected == i)) selected = i;
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) { renaming = i; renameInput = p.Name; }
                ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(count).X);
                ImGui.TextColored(Theme.Muted, count);
            }
            ImGui.PopID();
        }

        // New / rename / duplicate / delete, as buttons
        ImGui.Spacing();
        if (Ui.IconButton("new", FontAwesomeIcon.Plus, "New playlist"))
        {
            Playlists.Add(new JukeboxPlaylist { Name = $"Playlist {Playlists.Count + 1}" });
            selected = Playlists.Count - 1;
            renaming = selected;
            renameInput = Playlists[selected].Name;
            plugin.SaveConfig();
        }
        var p0 = Current;
        ImGui.SameLine();
        if (Ui.IconButton("rename", FontAwesomeIcon.Pen, "Rename the selected playlist (or double-click it)", p0 == null))
        {
            renaming = selected;
            renameInput = p0!.Name;
        }
        ImGui.SameLine();
        if (Ui.IconButton("dup", FontAwesomeIcon.Copy, "Duplicate the selected playlist", p0 == null))
        {
            Playlists.Insert(selected + 1, new JukeboxPlaylist
            {
                Name = p0!.Name + " (copy)",
                Songs = p0.Songs.Select(s => new JukeboxSong { Url = s.Url, Title = s.Title, Seconds = s.Seconds }).ToList(),
            });
            selected++;
            plugin.SaveConfig();
        }
        ImGui.SameLine();
        if (Ui.IconButton("del", FontAwesomeIcon.Trash, "Delete the selected playlist", p0 == null))
        {
            deleting = selected;
            ImGui.OpenPopup("confirmDelete");
        }
        if (ImGui.BeginPopup("confirmDelete"))
        {
            if (deleting >= 0 && deleting < Playlists.Count)
            {
                ImGui.TextUnformatted($"Delete \"{Playlists[deleting].Name}\" ({Playlists[deleting].Songs.Count} songs)?");
                ImGui.TextColored(Theme.Muted, "This can't be undone.");
                if (ImGui.Button("Delete"))
                {
                    Playlists.RemoveAt(deleting);
                    selected = Math.Clamp(selected, 0, Math.Max(0, Playlists.Count - 1));
                    deleting = -1;
                    plugin.SaveConfig();
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button("Keep it")) { deleting = -1; ImGui.CloseCurrentPopup(); }
            }
            ImGui.EndPopup();
        }

        ImGui.Spacing();
        Muted("Saved on your PC. Double-click a playlist to rename it.");
    }

    private void DrawSongs()
    {
        // Add or send: a link, or a YouTube search (works with or without a playlist)
        var p = Current;
        var playlistLink = Plugin.ParsePlaylist(addInput.Trim());
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - (playlistLink != null && p == null ? 210 : 150) * ImGuiHelpers.GlobalScale);
        var enter = ImGui.InputTextWithHint("##add", "> paste a YouTube or Spotify link (song or playlist), or search YouTube", ref addInput, 300, ImGuiInputTextFlags.EnterReturnsTrue);
        playlistLink = Plugin.ParsePlaylist(addInput.Trim());
        var link = LooksLikeLink(addInput);
        ImGui.SameLine();
        if (playlistLink != null)
        {
            // A YouTube playlist, or a Spotify playlist or album: all of its songs.
            if (importing) ImGui.BeginDisabled();
            if (ImGui.Button(p == null ? "+ New playlist" : "+ Add all") || (enter && !importing)) Import(playlistLink, p, send: false);
            Ui.Tooltip(p == null ? "Make a new playlist with all of its songs." : $"Add all of its songs to \"{p.Name}\".");
            ImGui.SameLine();
            if (!CanSend) ImGui.BeginDisabled();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Send")) Import(playlistLink, null, send: true);
            if (!CanSend) ImGui.EndDisabled();
            if (importing) ImGui.EndDisabled();
            SendTooltip(Link.Info is { } inf ? $"Send its songs straight to their queue (they take up to {inf.MaxSongs} at a time)." : "Send its songs straight to their queue.");
        }
        else if (link)
        {
            if (p == null) ImGui.BeginDisabled();
            var add = ImGui.Button("+ Add") || (enter && p != null);
            if (p == null) ImGui.EndDisabled();
            Ui.Tooltip(p == null ? "Make a playlist first, or send it straight away." : "Add it to this playlist.");
            if (add && p != null) { AddSong(p, addInput.Trim(), null, null); addInput = string.Empty; }
            ImGui.SameLine();
            if (!CanSend) ImGui.BeginDisabled();
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, "Send")) { SendSingle(addInput.Trim(), null, null); addInput = string.Empty; }
            if (!CanSend) ImGui.EndDisabled();
            SendTooltip("Send just this song to their queue.");
            // A song opened from a playlist (watch?v=...&list=...): offer the whole playlist too.
            if (ListInWatchLink(addInput) is { } fromList)
            {
                if (importing) ImGui.BeginDisabled();
                if (ImGui.SmallButton(p == null ? "This song is from a playlist: make a playlist with all of it" : $"This song is from a playlist: add all of it to \"{p.Name}\""))
                    Import(fromList, p, send: false);
                if (importing) ImGui.EndDisabled();
            }
        }
        else
        {
            if (ImGui.Button("Search") || enter) Search();
        }
        if (searchStatus.Length > 0) Muted(searchStatus);
        if (searchResults.Count > 0) DrawSearchResults(p);

        ImGui.Separator();
        if (p is null)
        {
            Label("NO_PLAYLIST");
            Muted("Make a playlist on the left to save songs. You can also send a song straight away from the box above.");
            return;
        }

        var total = p.Songs.Sum(s => s.Seconds ?? 0);
        Label($"{p.Name.ToUpperInvariant()} // {p.Songs.Count} TRACK{(p.Songs.Count == 1 ? "" : "S")}" + (total > 0 ? $" · {Ui.Length(total)}" : ""));
        var rowH = ImGui.GetFrameHeight();
        var buttons = (rowH + ImGui.GetStyle().ItemSpacing.X) * 4;
        for (var i = 0; i < p.Songs.Count; i++)
        {
            var s = p.Songs[i];
            ImGui.PushID(i);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"{i + 1:00}");
            ImGui.SameLine();
            var right = buttons + 50 * ImGuiHelpers.GlobalScale;
            ImGui.TextUnformatted(Ui.Ellipsize(s.Title ?? s.Url, ImGui.GetContentRegionAvail().X - right));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(s.Url);
            ImGui.SameLine(ImGui.GetContentRegionMax().X - right + 6 * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(Theme.Muted, Ui.Length(s.Seconds));
            ImGui.SameLine(ImGui.GetContentRegionMax().X - buttons);
            if (Ui.IconButton("send", FontAwesomeIcon.Play, CanSend ? "Send just this song to their queue" : SendBlockedReason(), !CanSend)) Link.SendOne(s);
            ImGui.SameLine();
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
        if (p.Songs.Count == 0) Muted("No songs yet: paste a link above, or type a few words to search YouTube.");
    }

    private string SendBlockedReason() =>
        Link.TargetName == null ? "Open the terminal on a player who runs Tundy to send songs."
        : !Linked ? "Not linked to their Tundy."
        : "They aren't taking songs right now.";

    private void SendTooltip(string ok)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(CanSend ? ok : SendBlockedReason());
    }

    private static bool LooksLikeLink(string s)
    {
        s = s.Trim();
        return Plugin.YouTubeRegex().IsMatch(s) || Plugin.SpotifyRegex().IsMatch(s);
    }

    private void Search()
    {
        var text = addInput.Trim();
        if (text.Length == 0 || searching) return;
        searching = true;
        searchStatus = $"Searching YouTube for \"{text}\"...";
        _ = Task.Run(async () =>
        {
            try
            {
                searchResults = await plugin.YtDlp.SearchAsync(text, 6, default);
                searchStatus = searchResults.Count == 0 ? "Nothing found." : "Add a result to the playlist (+), or send it straight away (play button):";
            }
            catch (Exception ex) { searchStatus = $"Search failed: {RetryPolicy.Short(ex.Message)}"; }
            finally { searching = false; }
        });
    }

    private void DrawSearchResults(JukeboxPlaylist? p)
    {
        foreach (var r in searchResults.ToList())
        {
            ImGui.PushID(r.Id);
            var url = $"https://www.youtube.com/watch?v={r.Id}";
            var title = TitleCleaner.Clean(r.Title);
            if (Ui.IconButton("add", FontAwesomeIcon.Plus, p == null ? "Make a playlist first" : "Add to this playlist", p == null))
            {
                AddSong(p!, url, title, r.Seconds);
                searchResults = searchResults.Where(x => x.Id != r.Id).ToList();
            }
            ImGui.SameLine();
            if (Ui.IconButton("send", FontAwesomeIcon.Play, CanSend ? "Send just this song to their queue" : SendBlockedReason(), !CanSend))
                SendSingle(url, title, r.Seconds);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Ui.Ellipsize(title, ImGui.GetContentRegionAvail().X - 60 * ImGuiHelpers.GlobalScale));
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, Ui.Length(r.Seconds));
            ImGui.PopID();
        }
        if (ImGui.SmallButton("Close results")) { searchResults = Array.Empty<YouTubeResult>(); searchStatus = string.Empty; }
    }

    // ---- playlist links

    private volatile bool importing;
    // Work from the background reader, done at the start of the next frame (the playlists are only changed while drawing).
    private readonly ConcurrentQueue<Action> uiWork = new();

    private static readonly System.Text.RegularExpressions.Regex WatchList =
        new(@"[?&]list=(?<list>[\w-]{10,})", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The playlist in a "watch?v=...&amp;list=..." link (not YouTube's endless Mixes), or null.</summary>
    private static PlaylistLink? ListInWatchLink(string s)
    {
        s = s.Trim();
        if (!Plugin.YouTubeRegex().IsMatch(s)) return null;
        var m = WatchList.Match(s);
        if (!m.Success) return null;
        var id = m.Groups["list"].Value;
        if (id.StartsWith("RD", StringComparison.Ordinal)) return null; // a Mix: never ends
        return new PlaylistLink("youtube", id, $"https://www.youtube.com/playlist?list={id}");
    }

    /// <summary>
    /// Reads a playlist link's songs, then adds them to <paramref name="into"/> (or a new playlist named after it),
    /// or with <paramref name="send"/> sends them straight to their queue.
    /// </summary>
    private void Import(PlaylistLink link, JukeboxPlaylist? into, bool send)
    {
        if (importing) return;
        importing = true;
        addInput = string.Empty;
        searchResults = Array.Empty<YouTubeResult>();
        searchStatus = "Reading the playlist...";
        _ = Task.Run(async () =>
        {
            try
            {
                // Their Tundy checks the length limit itself, so nothing is left out here for being long.
                var c = await PlaylistReader.ReadAsync(link, plugin.YtDlp, 24 * 3600, default);
                var songs = c.Songs
                    .Select(s => new JukeboxSong
                    {
                        Url = s.YouTubeUrl ?? $"https://open.spotify.com/track/{s.Spotify!.Id}",
                        Title = s.Title,
                        Seconds = s.Seconds,
                    })
                    .Where(s => JukeboxProtocol.IdFor(s.Url) != null)
                    .ToList();
                var left = c.Total - songs.Count;
                var note = left > 0 ? $" ({left} couldn't be used: live, private or not songs)" : "";
                uiWork.Enqueue(() => Imported(c.Name, songs, into, send, note));
            }
            catch (Exception ex)
            {
                searchStatus = $"Couldn't read that playlist: {RetryPolicy.Short(ex.Message)}";
                importing = false;
            }
        });
    }

    private void Imported(string name, List<JukeboxSong> songs, JukeboxPlaylist? into, bool send, string note)
    {
        importing = false;
        if (songs.Count == 0) { searchStatus = "That playlist has no songs Tundy can use." + note; return; }

        if (send)
        {
            if (!CanSend) { searchStatus = SendBlockedReason(); return; }
            // Only what they'd take: fewer tells.
            var max = Link.Info is { MaxSongs: > 0 } info ? info.MaxSongs : songs.Count;
            Link.Send(songs.Take(max), shuffle: false);
            searchStatus = $"Sending {Math.Min(max, songs.Count)} of {songs.Count} songs from \"{name}\"." + note;
            return;
        }

        var p = into != null && Playlists.Contains(into) ? into : null;
        if (p == null)
        {
            p = new JukeboxPlaylist { Name = name.Length > 0 ? name : $"Playlist {Playlists.Count + 1}" };
            Playlists.Add(p);
            selected = Playlists.Count - 1;
        }
        var have = p.Songs.Select(s => JukeboxProtocol.IdFor(s.Url)).ToHashSet();
        var added = 0;
        foreach (var s in songs)
        {
            if (!have.Add(JukeboxProtocol.IdFor(s.Url))) continue;
            p.Songs.Add(s);
            added++;
        }
        plugin.SaveConfig();
        var dupes = songs.Count - added;
        searchStatus = $"Added {added} song{(added == 1 ? "" : "s")} from \"{name}\" to \"{p.Name}\""
                       + (dupes > 0 ? $" ({dupes} already in it)" : "") + "." + note;
    }

    private void SendSingle(string url, string? title, int? seconds) =>
        Link.SendOne(new JukeboxSong { Url = url, Title = title, Seconds = seconds });

    private void AddSong(JukeboxPlaylist p, string url, string? title, int? seconds)
    {
        if (p.Songs.Any(s => JukeboxProtocol.IdFor(s.Url) == JukeboxProtocol.IdFor(url))) { searchStatus = "That song is already in this playlist."; return; }
        var song = new JukeboxSong { Url = url, Title = title, Seconds = seconds };
        p.Songs.Add(song);
        plugin.SaveConfig();
        if (title == null || seconds == null) LookUp(song, save: true);
    }

    /// <summary>Finds a song's name and length in the background (at most two at once).</summary>
    private void LookUp(JukeboxSong song, bool save)
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
                if (save) plugin.SaveConfig();
            }
            catch (Exception ex) { Plugin.Log.Debug(ex, $"Couldn't look up {song.Url}"); }
            finally { lookups.Release(); }
        });
    }

    private void DrawSendBar()
    {
        var p = Current;
        var canSend = CanSend && p is { Songs.Count: > 0 };
        if (!canSend) ImGui.BeginDisabled();
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.PrimaryButton);
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Play, $"Send the playlist to {Link.TargetName ?? "their"}'s queue")) Link.Send(p!.Songs, shuffle: false);
        ImGui.PopStyleColor();
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Random, "Shuffle & send")) Link.Send(p!.Songs, shuffle: true);
        if (!canSend) ImGui.EndDisabled();
        if (!canSend && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(!CanSend ? SendBlockedReason() : "Add songs to the playlist first.");
        DrawResult();
    }

    private void DrawResult()
    {
        if (Link.LastResult.Length > 0)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Link.LastResultOk ? Theme.Muted : Theme.Bad, "> " + Link.LastResult);
            ImGui.PopTextWrapPos();
        }
        else Muted("> Songs go in one hidden tell (or a few, for big playlists). Their rules apply: approval, limits, cooldowns.");
    }

    // ---- their queue tab

    private void DrawTheirQueueTab()
    {
        if (!Linked)
        {
            Muted(Link.TargetName == null
                ? "Open the terminal on a player who runs Tundy to see their queue."
                : "Not linked to their Tundy yet.");
            return;
        }

        // Ask for it when the tab is open and it's out of date (their info says it changed, or after 30 s).
        if (!Link.QueueRequested && (DateTime.Now - Link.TheirQueueAt).TotalSeconds > 30) Link.RequestQueue();

        Label("NOW_PLAYING");
        ImGui.TextUnformatted(Link.Info?.NowPlaying ?? "nothing");
        ImGui.Spacing();

        var ids = Link.TheirQueue;
        Label($"UP_NEXT // {ids.Count} SONG{(ids.Count == 1 ? "" : "S")}");
        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh")) Link.RequestQueue();
        ImGui.SameLine();
        if (ImGui.SmallButton("Save as a playlist")) Link.RequestQueue(save: true);
        Ui.Tooltip("Copies the songs in their queue into a new playlist of yours.");

        var height = ImGui.GetContentRegionAvail().Y - ImGui.GetTextLineHeightWithSpacing() * 2 - 8;
        ImGui.BeginChild("theirQueueList", new Vector2(0, height), true);
        if (Link.TheirQueueAt == DateTime.MinValue && Link.QueueRequested) Muted("Asking their Tundy for its queue...");
        else if (ids.Count == 0) Muted("Their queue is empty.");
        var p = Current;
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i];
            if (JukeboxProtocol.FromId(id) is not { } s) continue;
            var song = known.GetOrAdd(id, _ =>
            {
                var fresh = new JukeboxSong { Url = s.Url };
                LookUp(fresh, save: false);
                return fresh;
            });
            ImGui.PushID(id);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.Muted, $"{i + 1:00}");
            ImGui.SameLine();
            var right = ImGui.GetFrameHeight() + 60 * ImGuiHelpers.GlobalScale;
            ImGui.TextUnformatted(Ui.Ellipsize(song.Title ?? "(looking it up...)", ImGui.GetContentRegionAvail().X - right));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(song.Url);
            ImGui.SameLine(ImGui.GetContentRegionMax().X - right + 6 * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(Theme.Muted, Ui.Length(song.Seconds));
            ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.GetFrameHeight());
            if (Ui.IconButton("keep", FontAwesomeIcon.Plus, p == null ? "Make a playlist first" : $"Add to \"{p.Name}\"", p == null))
                AddSong(p!, song.Url, song.Title, song.Seconds);
            ImGui.PopID();
        }
        ImGui.EndChild();
        if (Link.TheirQueueAt > DateTime.MinValue) Muted($"> updated {Ui.Ago(Link.TheirQueueAt)}");
        DrawResult();
    }

    /// <summary>"Save as a playlist": their queue's songs become a new playlist.</summary>
    private void SaveTheirQueue(string name, List<string> ids)
    {
        var p = new JukeboxPlaylist { Name = $"{name}'s queue {DateTime.Now:MMM d}" };
        foreach (var id in ids)
        {
            if (JukeboxProtocol.FromId(id) is not { } s) continue;
            known.TryGetValue(id, out var k);
            p.Songs.Add(new JukeboxSong { Url = s.Url, Title = k?.Title, Seconds = k?.Seconds });
        }
        Cfg.JukeboxPlaylists.Add(p);
        selected = Cfg.JukeboxPlaylists.Count - 1;
        plugin.SaveConfig();
        foreach (var song in p.Songs.Where(x => x.Title == null || x.Seconds == null)) LookUp(song, save: true);
    }
}
