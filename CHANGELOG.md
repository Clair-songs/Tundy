# Tundy changelog

The newest version's notes show in Dalamud's plugin list when you update.

## 0.6.6
- The volume bar on Now playing is gone. While a shared song played, it changed the game's "Self" volume (all of
  your character's sounds), and nothing put it back afterwards. Tundy no longer changes any game sound setting.
- /tundy vol and /tundy mute now change only Tundy's own player (songs that play just for you).
- If your character's sounds are quiet, raise "Self" in System Configuration → Sound Settings.

## 0.6.5
- Songs now play at about the same volume: Tundy measures how loud each one is and turns loud songs down and quiet ones
  up, for you and for the in-game copy your pairs hear (Settings → Playback → Even out song volume, on by default).
- No more two "Now playing" lines in your chat while songs are announced.
- Approved songs keep the name and length found while they waited, so they aren't looked up again.
- The playlist cooldown moved to Settings → Playlists, next to the other playlist settings.
- Removed a few old settings that did nothing.

## 0.6.4
- The jukebox terminal is gone.
- The welcome guide caught up: pick any music mod, "tell people why a link can't play", a note on !skip and
  !skipall, and a theme picker.

## 0.6.3
- Hid the game's chat bubble that repeated your last message after a jukebox tell, and sent fewer of those tells.
- The jukebox terminal took playlist links.
