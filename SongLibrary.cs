using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Tundy;

/// <summary>
/// Keeps recently downloaded songs (by YouTube video id) so a song requested again, or replayed from History,
/// starts right away instead of downloading again. When the folder gets bigger than the limit, the songs played
/// longest ago are deleted first.
/// </summary>
public sealed class SongLibrary
{
    private readonly string dir;
    private readonly Configuration config;
    private readonly object gate = new();

    public SongLibrary(string dir, Configuration config)
    {
        this.dir = dir;
        this.config = config;
        Directory.CreateDirectory(dir);
    }

    public string Folder => dir;

    private sealed record Meta(string Title);

    /// <summary>True for files that live in the library (they're kept after playing).</summary>
    public bool Contains(string file) =>
        file.StartsWith(dir, StringComparison.OrdinalIgnoreCase);

    /// <summary>A saved song for this video, or null. Marks it as recently used.</summary>
    public (string File, string Title)? TryGet(string videoId)
    {
        lock (gate)
        {
            try
            {
                var meta = Path.Combine(dir, videoId + ".json");
                if (!File.Exists(meta)) return null;
                var file = Directory.GetFiles(dir, videoId + ".*")
                    .FirstOrDefault(f => !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                         && !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                                         && !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase));
                if (file == null) return null;

                var title = JsonSerializer.Deserialize<Meta>(File.ReadAllText(meta))?.Title ?? videoId;
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow); // most recently used
                File.SetLastWriteTimeUtc(meta, DateTime.UtcNow);
                return (file, title);
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, $"Song library lookup failed for {videoId}");
                return null;
            }
        }
    }

    /// <summary>Remembers a freshly downloaded song, then trims the folder to the size limit.</summary>
    public void Save(string videoId, string title, string keepFile)
    {
        lock (gate)
        {
            try { File.WriteAllText(Path.Combine(dir, videoId + ".json"), JsonSerializer.Serialize(new Meta(title))); }
            catch (Exception ex) { Plugin.Log.Debug(ex, "Couldn't save song info"); }
        }
        Prune(keepFile);
    }

    /// <summary>Size of the saved songs, in MB.</summary>
    public double SizeMb
    {
        get
        {
            try { return new DirectoryInfo(dir).GetFiles().Sum(f => f.Length) / 1024.0 / 1024.0; }
            catch { return 0; }
        }
    }

    /// <summary>Deletes the songs played longest ago until the folder fits the limit. Never deletes <paramref name="keepFile"/>.</summary>
    public void Prune(string? keepFile = null)
    {
        lock (gate)
        {
            try
            {
                var limit = (long)Math.Max(50, config.CacheSizeMb) * 1024 * 1024;
                var songs = new DirectoryInfo(dir).GetFiles()
                    .Where(f => !f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();
                var total = songs.Sum(f => f.Length);
                foreach (var f in songs)
                {
                    if (total <= limit) break;
                    if (keepFile != null && f.FullName.Equals(keepFile, StringComparison.OrdinalIgnoreCase)) continue;
                    total -= f.Length;
                    var id = Path.GetFileNameWithoutExtension(f.Name);
                    try { f.Delete(); } catch { /* in use: try next time */ }
                    try { File.Delete(Path.Combine(dir, id + ".json")); } catch { /* ignored */ }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "Song library cleanup failed");
            }
        }
    }

    /// <summary>Deletes every saved song (from the Settings tab).</summary>
    public void Clear(string? inUse = null)
    {
        lock (gate)
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles())
            {
                if (inUse != null && f.FullName.Equals(inUse, StringComparison.OrdinalIgnoreCase)) continue;
                try { f.Delete(); } catch { /* in use */ }
            }
        }
    }
}
