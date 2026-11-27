using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Tundy;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 5;

    /// <summary>Master switch for reacting to "!q" tells.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Playback volume, 0.0 - 1.0.</summary>
    public float Volume { get; set; } = 0.5f;

    /// <summary>The game's "Self" volume from before you muted an in-game song, to put back on unmute.</summary>
    public uint SavedSelfVolume { get; set; } = 100;

    /// <summary>True while Tundy has muted "Self" for a pause, so it can be put back even after a crash.</summary>
    public bool PausedMutedSelf { get; set; } = false;

    /// <summary>Show the small "Now playing" window with the song name and time.</summary>
    public bool ShowMiniPlayer { get; set; } = false;

    /// <summary>Keep the "Now playing" window from being moved by accident.</summary>
    public bool LockMiniPlayer { get; set; } = false;

    /// <summary>Pastel purple look for Tundy's windows. Off = Dalamud's normal look.</summary>
    /// <summary>Old (0.4.0-0.4.5) pastel on/off switch; off becomes Dalamud's look in <see cref="ThemeChoice"/> on load.</summary>
    public bool PastelTheme { get; set; } = true;

    /// <summary>Tundy's look: pastel purple (default), minimal, or Dalamud's own.</summary>
    public ThemeStyle ThemeChoice { get; set; } = ThemeStyle.Pastel;

    /// <summary>Show the welcome guide the next time the control panel opens (set for brand-new setups).</summary>
    public bool ShowWelcome { get; set; }

    /// <summary>Show the song's cover (its YouTube thumbnail) in the player.</summary>
    public bool ShowCoverArt { get; set; } = true;

    /// <summary>Smaller "Now playing" window: cover, name, time bar and the next song.</summary>
    public bool CompactMiniPlayer { get; set; }

    // ---- Honorific title ----

    /// <summary>Show the song that's playing as your character's title through the Honorific plugin.</summary>
    public bool HonorificTitle { get; set; }

    /// <summary>
    /// When each person last sent a playlist (UTC), so the playlist cooldown still counts after Tundy reloads or
    /// updates. Old entries are removed when Tundy loads.
    /// </summary>
    public Dictionary<string, DateTime> LastPlaylistRequests { get; set; } = new();

    /// <summary>Old (0.4.1-0.4.2) "include the artist" switch; moved into <see cref="HonorificShow"/> on load.</summary>
    public bool HonorificWithArtist { get; set; }

    /// <summary>What the title shows: 0 the song's name, 1 the song's name then the artist, 2 swap between them.</summary>
    public int HonorificShow { get; set; }

    /// <summary>How long each one shows when swapping, in seconds (5-60).</summary>
    public int HonorificSwapSeconds { get; set; } = 15;

    /// <summary>Above your name (Honorific's "prefix"), otherwise below it.</summary>
    public bool HonorificAbove { get; set; }

    /// <summary>Only while the song is shared with Lightless pairs (so the title matches what they hear).</summary>
    public bool HonorificOnlyWhenSharing { get; set; } = true;

    public bool HonorificUseColor { get; set; } = true;
    public System.Numerics.Vector3 HonorificColor { get; set; } = new(0.776f, 0.710f, 0.969f); // lavender
    public bool HonorificUseGlow { get; set; }
    public System.Numerics.Vector3 HonorificGlow { get; set; } = new(0.357f, 0.282f, 0.569f);

    /// <summary>Silence playback without losing the volume setting.</summary>
    public bool Muted { get; set; } = false;

    // ---- Sharing through Lightless Sync ----

    /// <summary>Share songs with nearby Lightless pairs (otherwise they play only for you).</summary>
    public bool ShareViaLightless { get; set; } = true;

    /// <summary>
    /// Where the song goes: 0 = a music mod's song slot, played by its dance (like SoundyQ; see the Queue* settings),
    /// 1 = an emote's own sound (<see cref="ShareEmoteCommand"/>).
    /// </summary>
    public int ShareTarget { get; set; } = 0;

    /// <summary>Emote whose sound carries the song (see <see cref="SongSharer.Emotes"/>).</summary>
    public string ShareEmoteCommand { get; set; } = "/fume";

    /// <summary>Seconds to wait after applying a song so Lightless can upload it and pairs can download it.</summary>
    public int ShareSyncDelaySeconds { get; set; } = 10;

    /// <summary>Do the emote automatically when a shared song starts.</summary>
    public bool ShareAutoEmote { get; set; } = true;

    /// <summary>Mute Tundy's own player while sharing, so you only hear the in-game copy (no echo).</summary>
    public bool ShareSilenceLocal { get; set; } = true;

    /// <summary>Encode shared songs at 22 kHz instead of 44 kHz: half the size, syncs faster, sounds duller.</summary>
    public bool ShareSmallerFiles { get; set; } = false;

    // ---- Announcements ----

    /// <summary>Post the song name and requester in a chat channel whenever a song starts. Off by default.</summary>
    public bool AnnounceSongs { get; set; } = false;

    /// <summary>Chat channel for announcements, e.g. "/yell".</summary>
    public string AnnounceChannel { get; set; } = "/yell";

    /// <summary>Announcement text; {title} and {requester} are filled in.</summary>
    public string AnnounceTemplate { get; set; } = Announcer.DefaultTemplate;

    /// <summary>
    /// Who can skip the current song by sending "!skip": 0 = nobody, 1 = only the person who requested it,
    /// 2 = anyone who's allowed to request songs.
    /// </summary>
    public int SkipPermission { get; set; } = 1;

    /// <summary>Tell the requester where their song is in the queue. Off by default.</summary>
    public bool ReplyQueuePosition { get; set; } = false;

    /// <summary>
    /// Tell the requester why their song can't be played (private, removed, age-restricted, region, too long,
    /// live...), so they can send another link. On by default, separate from the queue position replies.
    /// </summary>
    public bool ReplyWhyRejected { get; set; } = true;

    /// <summary>"!skipall": people can remove all of their own songs (playing, queued, waiting for approval).</summary>
    public bool AllowSkipAll { get; set; } = true;

    /// <summary>Build shared music-mod songs from YouTube's Opus audio instead of its AAC (slightly better sound).</summary>
    public bool SharePreferOpus { get; set; } = true;

    // ---- Downloads ----

    /// <summary>Keep recently downloaded songs so repeats and History replays start instantly.</summary>
    public bool CacheSongs { get; set; } = true;

    /// <summary>Most disk space the saved songs may use, in MB.</summary>
    public int CacheSizeMb { get; set; } = 500;

    /// <summary>When yt-dlp was last updated automatically (it's updated once a day).</summary>
    public DateTime LastYtDlpUpdateUtc { get; set; } = DateTime.MinValue;

    // ---- Retries ----

    /// <summary>Try again automatically when a song's download fails.</summary>
    public bool AutoRetry { get; set; } = true;

    /// <summary>How many extra tries a failed song gets.</summary>
    public int RetryAttempts { get; set; } = 2;

    // ---- Playlists ----

    /// <summary>Accept YouTube playlists and Spotify playlists/albums.</summary>
    public bool AllowPlaylists { get; set; } = true;

    /// <summary>Most songs one playlist request can add.</summary>
    public int MaxPlaylistSongs { get; set; } = 15;

    /// <summary>Shuffle a playlist's songs before queueing them (then the limit picks a random selection).</summary>
    public bool ShufflePlaylists { get; set; } = false;

    /// <summary>Longest video (in seconds) that will be accepted.</summary>
    public int MaxDurationSeconds { get; set; } = 600;

    /// <summary>Maximum number of songs waiting in the queue.</summary>
    public int MaxQueueLength { get; set; } = 20;

    /// <summary>Seconds a single sender must wait between requests.</summary>
    public int PerSenderCooldownSeconds { get; set; } = 30;

    /// <summary>Seconds a single sender must wait between playlist requests (on top of the normal cooldown).</summary>
    public int PlaylistCooldownSeconds { get; set; } = 600;

    /// <summary>Accept Spotify track links (played via a matching YouTube upload).</summary>
    public bool AllowSpotify { get; set; } = true;

    /// <summary>Print "Now playing / queued" messages in your own chat log.</summary>
    public bool EchoToChat { get; set; } = true;

    // Before version 5 (TellTunes): allow list and block list. Moved into QueuePlayerRules / QueueAccessMode.
    public bool UseAllowList { get; set; } = false;
    public List<string> AllowedSenders { get; set; } = new();
    public List<string> BlockedSenders { get; set; } = new();

    /// <summary>Optional custom path to yt-dlp.exe. Empty = auto-managed copy.</summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>Delete downloaded audio files after they finish playing.</summary>
    public bool DeleteAfterPlay { get; set; } = true;

    // ---- One-time imports ----

    /// <summary>TellTunes' settings were copied in (or there were none to copy). Only happens once.</summary>
    public bool ImportedTellTunes { get; set; } = false;

    /// <summary>Soundy's settings were copied in (or there were none to copy). Only happens once.</summary>
    public bool ImportedSoundy { get; set; } = false;

    // ---- Song queue (Tundy's own version of SoundyQ) ----

    /// <summary>Tell commands people can request songs with, e.g. "!q", "!play".</summary>
    // Replace, not add to, the defaults when loading saved settings (otherwise each load adds the default again).
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<string> QueueAliases { get; set; } = new() { "!q" };

    /// <summary>Accept the shuffle commands below.</summary>
    public bool AllowShuffleCommand { get; set; } = true;

    /// <summary>Tell commands that queue a playlist shuffled, e.g. "!shuffle" (otherwise they work like a request).</summary>
    // Replace, not add to, the defaults when loading saved settings (otherwise each load adds the default again).
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<string> ShuffleCommands { get; set; } = new() { "!shuffle" };

    /// <summary>Who can request songs (players in <see cref="QueuePlayerRules"/> get their own setting instead).</summary>
    // New setups start with approval (existing settings files keep the value they saved).
    public AccessMode QueueAccessMode { get; set; } = AccessMode.EveryonePreQueue;

    /// <summary>Players with their own request permission, overriding <see cref="QueueAccessMode"/>.</summary>
    public List<QueuePlayerRule> QueuePlayerRules { get; set; } = new();

    /// <summary>Penumbra mod (folder name) whose song slot carries the song.</summary>
    public string QueueTargetMod { get; set; } = MegaMusicMod.RecommendedMod;

    /// <summary>Game sound files in that mod that carry the song, e.g. "sound/lolo.scd".</summary>
    // Replace, not add to, the defaults when loading saved settings (otherwise each load adds the default again).
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<string> QueueScdRoutes { get; set; } = new() { MegaMusicMod.RecommendedRoute };

    /// <summary>Emote whose dance plays the music mod's song slot.</summary>
    public string QueueEmoteCommand { get; set; } = MegaMusicMod.RecommendedEmote;

    /// <summary>
    /// Between requested songs, keep the music mod's song slot silent (true), or let the track picked in the
    /// music mod play when you dance (false).
    /// </summary>
    public bool BetweenSongsSilence { get; set; } = true;

    // ---- Other music mods (Sharing → Music mod) ----

    /// <summary>Use the dance that's turned on in Penumbra (true), or always the one picked in <see cref="OtherModDance"/>.</summary>
    public bool OtherModFollow { get; set; } = true;

    /// <summary>The picked dance: "group\u0001option" (see DanceSong.Key).</summary>
    public string OtherModDance { get; set; } = "";

    /// <summary>Also list dances whose sound is a long sound effect, not music.</summary>
    public bool OtherModShowEffects { get; set; }

    /// <summary>
    /// Song file(s) and emote set by hand (Sharing → Music mod → By hand) instead of from a scanned dance, so
    /// nothing changes them. Mods without a dance Tundy can find always work this way.
    /// </summary>
    public bool OtherModByHand { get; set; }

    /// <summary>The Import tab adds songs to the same mod and song file(s) as Sharing (instead of its own choice).</summary>
    public bool ImportFollowsSharing { get; set; } = true;

    // ---- Importer ----

    /// <summary>Penumbra mod (folder name) songs are imported into.</summary>
    public string ImportTargetMod { get; set; } = MegaMusicMod.RecommendedMod;

    /// <summary>Game sound files in that mod the imported song goes on.</summary>
    // Replace, not add to, the defaults when loading saved settings (otherwise each load adds the default again).
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<string> ImportRoutes { get; set; } = new() { MegaMusicMod.RecommendedRoute };

    /// <summary>Pick the imported song in the mod right away.</summary>
    public bool ImportSelectAfter { get; set; } = true;

    // ---- Importer (Soundy's mod import tools; used from a later update on) ----

    /// <summary>Penumbra's mod folder, e.g. "C:\Penumbra".</summary>
    public string PenumbraPath { get; set; } = string.Empty;

    /// <summary>Loudness for imported songs: 1.0 = original, higher boosts with a limiter.</summary>
    public float ImportVolume { get; set; } = 1.0f;

    /// <summary>EQ preset for imported songs (Soundy's numbering).</summary>
    public int ImportEqPreset { get; set; } = 0;

    /// <summary>Sound bus choice for imported songs (Soundy's numbering).</summary>
    public int ImportBusChoice { get; set; } = 0;

    /// <summary>Import from YouTube (0) or a local file (1) by default.</summary>
    public int ImportSource { get; set; } = 0;
}

/// <summary>Who can request songs. Same numbers as SoundyQ's setting, so imported values keep their meaning.</summary>
public enum AccessMode
{
    /// <summary>Everyone can request; requests wait for your approval.</summary>
    EveryonePreQueue = 0,
    /// <summary>Only players on your list can request.</summary>
    ListedOnly = 1,
    /// <summary>Everyone can request; requests go straight into the queue.</summary>
    EveryoneAutoApprove = 2,
}

/// <summary>One player's own request permission. Same numbers as SoundyQ's.</summary>
public enum RequestPermission
{
    /// <summary>Their requests wait for your approval.</summary>
    PreQueue = 0,
    /// <summary>Their requests go straight into the queue.</summary>
    AutoApprove = 1,
    /// <summary>Their requests and !skip are ignored.</summary>
    Blocked = 2,
}

/// <summary>One player's song-request permission.</summary>
[Serializable]
public class QueuePlayerRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Home world, or empty for any world.</summary>
    public string World { get; set; } = string.Empty;
    public RequestPermission Permission { get; set; } = RequestPermission.AutoApprove;
    public bool Enabled { get; set; } = true;
}
