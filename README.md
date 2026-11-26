# Tundy (Dalamud plugin)

Tundy is TellTunes and Soundy in one plugin. People send you songs by tell (`!q <link>`), and Tundy plays them for
you and, through the Mega Music Mod and Lightless Sync, for the people around you. Since version 0.2 it has its
own song queue (like SoundyQ), and since 0.3 Soundy's import tools too, so Soundy isn't needed any more.

## Install
1. In game, open `/xlsettings` → **Experimental**.
2. Under **Custom Plugin Repositories**, paste this URL, click **+**, then **Save and Close**:

   `https://raw.githubusercontent.com/Clair-songs/Tundy/main/pluginmaster.json`

3. Open `/xlplugins`, search for **Tundy** and click **Install**. Updates arrive there like any other plugin.
4. Type `/tundy` to open it. People can then send you songs with `/tell <you> !q <link>`.

Sharing songs with other players needs Penumbra and Lightless Sync (and the Mega Music Mod for the DJ dance).

**Roadmap**
1. ✅ Everything TellTunes does, under the new name, with your TellTunes and Soundy settings copied in.
2. ✅ Built-in song queue (like SoundyQ): request commands, approval queue, who can request, per-player
   permissions, duplicate check, `!q list`, playing through the Mega Music Mod's DJ dance.
3. ✅ Tool manager: Tundy downloads its own ffmpeg; music mod songs are stereo Vorbis like the mod's own.
4. ✅ Importer: YouTube or local audio into any Penumbra mod, with Soundy's loudness and EQ presets.
5. ✅ Sound replacer for any game sound, and clean-up tools.

Not included from Soundy: adding a sound to an animation that has none (Soundy edits the animation file) and its
VFX editor. Both are built on VFXEditor's file libraries, which would have to be built for each game version. Tundy's
importer and sound replacer put songs on sound files that animations and the game already play.

## Moving over from TellTunes and Soundy
- The first time Tundy loads, it copies all of TellTunes' settings, plus SoundyQ's setup (request commands, who can
  request, player list, music mod, sound route, emote) and Soundy's import settings (Penumbra folder, loudness, EQ).
  TellTunes' and Soundy's own settings files are only read, never changed. This happens once.
- Turn **TellTunes off** in `/xlplugins`. Turn **SoundyQ off** in `/soundyq` (Soundy itself can stay on for its
  import tools). While either one is also listening for the same tells, Tundy ignores requests and shows a red
  warning, because both would queue the same song.
- TellTunes' allow and block lists become players in the People tab (auto-approve and blocked).
- Tundy keeps its own copy of yt-dlp and its own song cache, so the first song may take a moment longer.

## License and credits
Tundy is free software under the **GNU Affero General Public License v3.0 or later** (see `LICENSE`). If you give
it to someone, they're entitled to its source code.
- Started from TellTunes.
- The import tools follow [Soundy](https://github.com/lnjanos/soundy-release) by kcuY (AGPL-3.0): its EQ presets
  and loudness curve are used as they are. The SoundyQ-style queue and the rest are written fresh for Tundy.
- ffmpeg builds by [gyan.dev](https://www.gyan.dev/ffmpeg/builds/).
- Made for [Yue & Lu's] Mega Music Mod by Yue Kamui and Lusius Rayer.
- Downloads use [yt-dlp](https://github.com/yt-dlp/yt-dlp) and [deno](https://deno.com).

## Building it yourself (dev plugin)
1. Build: `dotnet build -c Release` (output: `bin\Release\Tundy.dll`).
2. In game: `/xlsettings` → **Experimental** → **Dev Plugin Locations** → add the full path to `bin\Release\Tundy.dll` → Save.
3. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** → enable **Tundy**.

The first time it loads, it downloads `yt-dlp.exe` from the official yt-dlp GitHub releases into the plugin's config
folder. You don't need ffmpeg, because it picks m4a audio, which Windows can play natively.

## Importing songs into mods (Import tab, `/tundy import`)
Soundy's import tools: add a song to a mod for good, not just for one request.
- **Song**: a YouTube link or a file on your PC (mp3, m4a, ogg, opus, wav, flac...), an optional name, **Loudness**
  (1.0 = as it is, up to 5.0, with a limiter so it doesn't distort) and **EQ** (Soundy's presets: Neutral, Club /
  Bass, EDM, Rock, Pop, Orchestral / Score, Vocal Boost, Chill / Lo-Fi). Yours were copied from Soundy.
- **Add it to a mod**: by default the same mod and song file as Sharing → Music mod ("Same mod and song file as
  Sharing"), so a song you import is one the dance can play between requests. Untick it to pick another mod and the
  sound file(s) it should play on. Tundy lists the sound files the mod uses,
  most used first (for the Mega Music Mod: `sound/lolo.scd`, the DJ dance's song). The song becomes a new option in
  the mod's **Tundy imports** group, converted like the mod's own sound there (stereo Vorbis music, or mono ADPCM
  for sound effects), and can be picked right away.
- Songs added with Tundy are listed under the button: pick one, remove it (hold Ctrl), or turn imports off.
- Before changing another mod's files the first time, Tundy copies its `.json` files to `tundy-backup` inside it.
- While Tundy plays requests through the same music mod, its song slot is silent between requests. To hear a song
  you picked there, set Sharing → **Between requests, the dance plays** to "The song picked in the music mod".

## Sound replacer (Import tab)
Puts the song on any game sound file, e.g. an emote's sound (`sound/...scd`), through Tundy's own **Tundy Sounds**
mod (on top of normal mods, under Tundy Song). Your Lightless pairs get it like any mod. Each replaced sound is listed
with a button to give it its normal sound back.

## Download tools and clean up (Settings)
- **yt-dlp** (downloads, updates daily), **deno** (helps yt-dlp with YouTube) and **ffmpeg** (converts songs for
  the music mod and imports) are downloaded automatically. ffmpeg comes from the official Windows builds at gyan.dev
  (about 120 MB) and is checked against its published SHA-256. Until Tundy has its own, it uses Soundy's copy.
- **Clean up**: clear temporary files, delete old song files from the Tundy Song mod, delete SoundyQ's old songs
  from the music mod (hold Ctrl), and **Check imported songs** for missing files or mods that were renamed, with a
  button to remove broken entries (hold Ctrl).

## Requests
- Friends send: `/tell You@World !q https://youtu.be/xxxxxxxxxxx`. The request commands are set in the **People**
  tab (yours were copied from SoundyQ: `!ytqueue`, `!q`, `!play`, `!p`).
- `!q list` answers with what's playing, how many songs are waiting and where theirs is.
- `!shuffle <playlist link>` queues a playlist shuffled, whatever the "Shuffle playlists" setting says; with the
  max songs per playlist that's a random selection from the whole playlist. A single song link is queued normally,
  and `!shuffle` alone does nothing (it never reorders other people's songs). Same rules as `!q` (who can request,
  approval, cooldown). Change the shuffle commands, or turn them off, in the People tab.
- The same YouTube video or Spotify track can't be in the queue twice ("That song is already in the queue.").
- Spotify works too: `!q https://open.spotify.com/track/...` or a `spotify.link/...` share link from the phone app.
  Spotify audio is DRM-protected, so Tundy reads the song's title, all artists and exact length from its Spotify page,
  searches YouTube twice (cleaned title and exact title), and scores every result: matching length counts most, then
  title words and artists, with a bonus for official uploads and points off for live, cover, sped-up, nightcore,
  karaoke and 1-hour versions unless the Spotify track is that version. You can turn Spotify off in Settings.

## Who can request (People tab)
- **Anyone, straight into the queue** (SoundyQ's "Everyone Auto-Approve"), **Anyone, but I approve each request
  first** ("Everyone to Pre-Queue"), or **Only the players listed below** ("Listed Only").
- **Players**: give someone their own setting: *Auto-approve*, *Needs approval* or *Blocked*, optionally for one
  world only. Right-click any name anywhere in the panel to change it.
- Blocked people's requests and `!skip` are ignored.

## Approving requests
Requests that need approval wait in the **Now playing** tab, with the song's name looked up for you:
approve, approve and always approve that person, or reject. **Approve all** is there too, and so is
`/tundy approve` (oldest first) or `/tundy approve all`. A pill at the top of the panel shows how many are waiting. With replies
on, people hear "Your request is waiting for approval.", then "Your request was approved. ..." or "Sorry, your
request wasn't accepted."

## Letting other people hear it (Sharing tab)
FFXIV can't send audio to other players. Instead, Tundy puts each song on your character as a Penumbra mod, Lightless
Sync sends it to the people you're paired with, and your character does an emote that plays it. They only need
Lightless, not Tundy.

**Through the Mega Music Mod (recommended, how SoundyQ does it).** The Mega Music Mod's DJ dance on `/cheerjumpred`
plays one sound file, `sound/lolo.scd`. For each song Tundy:
1. turns it into a game sound file, using one of the music mod's own tracks as the template (versions of the mod
   without songs, like the **No songs Ver.**, work too: Tundy then uses the song slot's settings it ships with),
2. puts it on `sound/lolo.scd` in its own **Tundy Song** mod, which wins over the music mod (the music mod's files
   are never changed), and redraws you,
3. waits a few seconds (default 10) so Lightless can upload it and your pairs can download it,
4. does `/cheerjumpred motion` once (no chat message).

Between songs, Tundy Song holds a short silent track on that file, so the dance (which keeps going after a song)
doesn't play one of the music mod's own songs, and Lightless keeps knowing the file. One-time setup: in the Sharing tab click **Set up Mega Music Mod for me** (turns the mod on with
its "Dj (Cheer Jump)" dance and the DJ deck for your race). The music mod, its song file and emote are all chosen in one
place: Sharing → **Music mod** (for the Mega Music Mod there's also "Song file and emote by hand"). The problems banner warns when the mod's dance isn't set to "Dj (Cheer Jump)",
since only that dance plays the song slot on `/cheerjumpred`.

**Through other dance or music mods.** Sharing → **Music mod**: pick any Penumbra mod and Tundy reads which
of its dances play a song, on which emote (from the game's own emote data) and from which sound file, and lists them
(music first; long sound effects such as voices only when you tick "Show sound effects too"). Then either follow the
dance that's turned on in Penumbra (Tundy switches with it), always use one you pick, or set the song file and emote by
hand (then nothing changes them; mods where Tundy finds no dance always work this way). **Set up** turns the mod and that
dance on in your collection. Songs are built in the same format as the mod's own file: streamed stereo music like the Mega
Music Mod, or mono sound effects where the dance plays one, so they aren't silent. Big mods take a few seconds to scan.

**On one emote's own sound.** Works without a music mod: the song replaces an emote's normal sound while it plays
(default `/fume`). `/cheerjumpred` is in that list too, set up to swap its start animation for Linkpearl's, whose
sound carries the song.

Limits:
- Only people paired with you in Lightless (directly or through a Syncshell) and near you hear it.
- The first time, your character does the emote once with its normal sound so Lightless learns the file. After that, songs start sooner.
- Seeking is off while sharing, because it can't change the in-game copy. Skip and stop work.
- Music mod songs are stereo Ogg Vorbis like the mod's own tracks (about 1.2 MB per minute), made with ffmpeg.
  Emote songs are mono ADPCM (about 1.4 MB per minute, 0.7 MB with "Smaller files").
- The music mod's song slot only plays looping sounds. Tundy adds 4 seconds of silence to the end of each song and
  loops only that, so the song plays once and then stays quiet while you keep dancing.
- If you're still dancing when the next song is ready, the dance keeps going: Tundy puts the song on without a
  redraw, waits the sync time, then does one redraw, which restarts the dance with the new song (no bow, no new
  emote). If you stopped dancing, the song starts with the emote as usual. Skipping a song puts silence on the song
  slot and redraws you, which cuts the song off; if you're dancing, you keep dancing.

## Skipping with `!skip` and `!skipall`
People can send `/tell <you> !skip` to skip the song that's playing. Settings → **Skipping with !skip and !skipall** →
**Who can skip**: only the person who requested the song (default), anyone who's allowed to request songs, or
nobody. Blocked people can't skip, one person can skip at most once every 10 seconds, and every `!skip` shows in
the Requests log (Queue tab).

`/tell <you> !skipall` removes all of that person's own songs, and nobody else's: the one playing (if it's
theirs), their songs waiting in the queue (playlist songs too) and their requests waiting for your approval. Songs you
added yourself never count as theirs. It's on by default ("Let people remove all their own songs with !skipall") and
works even when `!skip` is set to nobody; the same 10-second wait applies, and blocked people can't use it. With replies
on they're told how many were removed.

## Jukebox terminal (Tundy to Tundy)
Right-click another player and pick **Tundy: open jukebox terminal** (or target them and type `/tundy jukebox`). If they run
Tundy too, the terminal links to theirs and shows what's playing, how long their queue is, and their rules (approval,
cooldown, max songs per send). If they don't, it says LINK FAILED: it only works between two Tundys.

- **Your playlists**, saved on your PC: make, rename (double-click), duplicate and delete them; add songs by pasting a
  YouTube or Spotify link or by searching YouTube; reorder or remove songs. Names and lengths are filled in for you.
- **Send to their queue** or **Shuffle & send**: their Tundy treats it like a playlist request from you, so their rules
  apply (who can request, approval, max songs per playlist, playlist cooldown). With approval, it waits on their Now
  playing tab as one "Jukebox: N songs" request.
- **Save their queue** copies the songs in their queue into a new playlist of yours.
- The terminal uses your Tundy theme (pastel, minimal or Dalamud's own look).

How it talks: there's no other channel between two players' plugins, so the two Tundys use tells. Each tell starts with
`~tdy1` and both Tundys hide those tells from chat. Songs travel as short YouTube/Spotify ids, about 35 per tell, so a
playlist is one tell (or a few for big ones), spaced out like Tundy's other replies. It works wherever you could send
them a tell. To stop other players' terminals from sending you songs, untick Settings → Jukebox terminal → "Let other
Tundy players send me songs".

## Replying to requesters (optional, off by default)
Settings → **Reply to requesters**: after a request is accepted, the requester gets a tell such as "Your song is up
next!" or "Your song is in the queue. 3 songs play before it." It's sent with `/tell Name@World` to that exact
person, because `/r` goes to whoever messaged you last. At most one tell every 2 seconds.

## Announcing songs (optional, off by default)
Settings → **Announce songs in chat**: when a song starts, your character posts e.g.
`/yell Now playing: Blinding Lights - requested by Alex Example`.
- Channel: Yell (default), Say, Shout, Party or Free Company.
- Message template with `{title}` and `{requester}`, a live preview, and a **Send a test message** button.
- Song titles come from other people, so line breaks and `<` `>` are removed (the game would turn `<pos>`/`<t>`
  into your position or target), long titles are shortened, and there's at most one announcement every 20 seconds.

## Playlists
Send a playlist like a song: `!q <playlist link>`.
- **YouTube**: `youtube.com/playlist?list=...` (also YouTube Music). A `watch?v=...&list=...` link still plays just
  that one video, and endless YouTube Mixes (`list=RD...`) are refused.
- **Spotify**: playlists and albums (`open.spotify.com/playlist/...`, `/album/...`, `spotify:playlist:...`, or a
  `spotify.link/...` share link). Songs are read from Spotify's public embed page (no account needed) and each one
  is matched to YouTube like a single Spotify song. Private playlists can't be read.
- Live streams and songs longer than the max song length are left out.
- **Shuffle playlists** mixes up the songs first; **Max songs per playlist** keeps one tell from filling the queue.

## Downloads and song names
- yt-dlp is told to use UTF-8, so titles in other alphabets show correctly.
- Song names are cleaned up everywhere (panel, announcements, replies): "(Official Video)", "[Lyrics]",
  "| Official Music Video", "Official MV", "HD/4K", "Remaster" are removed; "feat.", "Remix", "Live", "Version" stay.
- yt-dlp updates itself once a day, and uses deno (Soundy's copy if you have Soundy, otherwise Tundy downloads the
  official release once) to get past YouTube's download checks.
- Recent songs are kept (Settings, default 500 MB) so repeats and History replays start instantly.
- Failed downloads are retried automatically for temporary problems (network, HTTP 403, bot checks, timeouts), with
  a yt-dlp update before the last try. Songs that can never play (private, age-restricted, too long, live...) aren't.
- When YouTube refuses a download with HTTP 403 or 503 (it often does over IPv6), Tundy tries again right away over
  IPv4 and keeps using IPv4 for the rest of the session. If YouTube still refuses, Tundy waits a moment and tries gently: one
  piece of the song at a time instead of four, with longer waits between yt-dlp's own retries. When that works, it keeps
  downloading gently for the rest of the session.
- When a song can't be played, Tundy says why in plain words: a private video, removed, age-restricted, not available in
  this region, too long (with its length and the limit), a live stream, a premiere that hasn't started, members-only, or no
  matching song on YouTube. By default the requester gets a tell too ("Sorry, your song can't be played: it's a private
  video. Try another link."); turn that off in Settings → Reply to requesters.
- **Retry by hand:** songs and playlists that still failed after the automatic retries stay in a **Couldn't play** list on
  the Queue tab, with **Retry** (it goes in as the next song, still for the same person). Now playing shows the newest one,
  failed lines in the Requests log get a retry button, and `/tundy retry` retries the newest. Videos that can never play
  (private, removed, too long...) say so instead of offering Retry.

## Song as your title (Honorific)
With the [Honorific](https://github.com/Caraxi/Honorific) plugin installed, Settings → Song as your title can show the
song that's playing as your character's title: just the song's name (《♪ Innerbloom》), the song's name then the artist
(《♪ Innerbloom · RÜFÜS DU SOL》), or the two taking turns (every 15 seconds; 5 to 60 in Settings) (《♪ Innerbloom》, then
《by RÜFÜS DU SOL》). It goes above or below your name,
in a color and glow you pick. It's off by default. Honorific titles can be at most 32 characters, so long names are
cut short (taking turns gives each one the full 32). Your pairs may see the turns later or skip one, since Honorific
passes title changes on at most every 5 seconds. When the music stops (3 seconds after the last song, so it doesn't flicker between songs) your own
Honorific title comes back; Honorific's settings are never changed and nothing is saved. Lightless pairs who also have
Honorific see it a few seconds later. By default it only shows while the song is shared with them.

## First start
On a new setup, `/tundy` opens a short welcome guide (Settings → Look → Open the welcome guide again brings it
back): 1) who can send you songs (approval first is preselected) and the request command, 2) who hears the music (just you,
or your Lightless pairs too, with the same checks, "Set it up for me" and **Test now** as the Sharing tab), 3) a few extras
(announce songs, replies, the Honorific title), 4) paste a first song and copy the line to give your friends. Song requests
stay paused until you finish it, so nobody's link plays before you've picked who can send them. While yt-dlp, deno and
ffmpeg download the first time, a **Getting ready** card shows each one (ffmpeg only when you share through the music mod).
Existing setups don't see the guide and keep their settings.

## In-game control panel (`/tundy`)
Three looks in Settings → Look → Theme, switchable any time: **Pastel purple** (the default: dark plum, lavender, mint
and pink), **Minimal** (near-black and greys, text-only tabs with a line under the open one, outlined buttons, colour only
on small status dots), or **Dalamud's own look**. The mini player and the welcome guide follow the same choice.
- **Problems banner**: shows only when something's wrong, with a button that fixes it or opens where to fix it: TellTunes
  or SoundyQ also on (SoundyQ also counts when it holds the music mod's song slot, which beats Tundy's even with
  different request commands), Penumbra not running, the music mod not set up, ffmpeg missing, the emote not learned,
  or the last song didn't play for others.
- **Top**: pills for accepting/paused requests, who can request, how songs are shared, and requests to approve. Click one to open its settings.
- **Now playing**: the song's cover (its YouTube thumbnail, can be turned off in Settings → Look) and name, who sent it, a seek bar, skip/stop and volume; requests waiting for your approval, with song lengths; the next 3 songs with their lengths and how much music is queued; and a box to paste a link yourself.
- **Queue**: play now, move up/down, remove, shuffle, clear (hold Ctrl), plus **History** (play again, re-queue, copy the link) and the **Requests log** (every request tell and what happened to it) as sections you can open.
- **People**: request and shuffle commands, who can request, and per-player settings.
- **Sharing**: setup in steps (Penumbra and Lightless, sharing on, the music mod or emote, then **Test now**, which does
  the emote once and checks the game loads Tundy's sound for your character), then sections for how it plays, starting
  songs and good-to-know notes.
- **Import**: add songs to mods, and the sound replacer.
- **Settings** (groups you can open and close): look (theme: pastel, minimal or Dalamud's; covers), requests and cooldowns, the Honorific title, announcements, skipping, replies, retries, playlists, playback, the Now playing window, download tools, clean up.

**Now playing window** (`/tundy mini`, or the window button at the top right of the panel): the song's name, a
progress bar with elapsed / total time, skip and panel buttons, and who sent it. **Compact** (Settings → Now playing
window, or right-click the window): cover, name, a thin time bar, skip and the next song, without a title bar.

Chat commands: `/tundy mini | jukebox | play <link> | skip | stop | retry | approve [all] | import | on | off | share | mute | vol 0-100`

## Sharing Tundy with other people
`dotnet build -c Release` makes `bin\Release\Tundy\latest.zip` (the plugin and everything it needs) and
`bin\Release\Tundy\Tundy.json`.
- **Quick (friends):** send them `latest.zip`. They unzip it into a folder, add the path to `Tundy.dll` in
  `/xlsettings` → Experimental → Dev Plugin Locations, and turn it on in `/xlplugins` → Dev Tools.
- **With updates:** host a custom plugin repository with a `pluginmaster.json` that points at `latest.zip`. People
  add its URL in `/xlsettings` → Experimental → Custom Plugin Repositories.
- yt-dlp is downloaded on first use; the Mega Music Mod, Penumbra and Lightless are optional.

## Safety defaults (change them in the window)
- Only YouTube and Spotify links are accepted, so senders can't make yt-dlp fetch other sites.
- Max song length 10 min, no live streams, queue of up to 20, 30 s cooldown per sender, and 10 min between
  playlists from the same person (Settings → Requests → Playlist cooldown).
- Only plain emote commands (`/word`) can be set as the music mod's emote.
- If downloads start failing, click **Update yt-dlp** (YouTube changes often).
