using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tundy;

/// <summary>One YouTube search result.</summary>
public sealed record YouTubeResult(string Id, string Title, int? Seconds, string? Channel, long? Views = null, string? Description = null)
{
    /// <summary>YouTube's own auto-generated upload of the official recording ("Artist - Topic" channels).</summary>
    public bool IsOfficialAudio =>
        (Description?.StartsWith("Provided to YouTube", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Channel?.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>A downloaded song: the file, the raw video title, and YouTube's artist/track fields when it has them.</summary>
public sealed record SongDownload(string File, string Title, string? Artist, string? Track)
{
    /// <summary>The name to show: "Artist - Track" when YouTube knows them, otherwise the cleaned-up video title.</summary>
    public string DisplayTitle => TitleCleaner.Display(Title, Artist, Track);
}

/// <summary>Thin wrapper around the yt-dlp command line tool.</summary>
public sealed class YtDlp
{
    // Official release assets of the yt-dlp and Deno projects.
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

    private readonly string toolDir;
    private readonly Func<string> customPath;
    private readonly SemaphoreSlim installLock = new(1, 1);
    private readonly SemaphoreSlim denoLock = new(1, 1);

    public YtDlp(string toolDir, Func<string> customPath)
    {
        this.toolDir = toolDir;
        this.customPath = customPath;
        Directory.CreateDirectory(toolDir);
    }

    private string ManagedExe => Path.Combine(toolDir, "yt-dlp.exe");
    private string ManagedDeno => Path.Combine(toolDir, "deno.exe");

    // Soundy ships its own deno; using it saves a ~40 MB download when Soundy is installed.
    private string SoundyDeno => Path.Combine(Directory.GetParent(Directory.GetParent(toolDir)!.FullName)!.FullName, "Soundy", "tools", "deno.EXE");

    public string ExePath
    {
        get
        {
            var custom = customPath();
            return string.IsNullOrWhiteSpace(custom) ? ManagedExe : custom;
        }
    }

    public bool IsInstalled => File.Exists(ExePath);

    /// <summary>
    /// The JavaScript runtime yt-dlp uses to get past YouTube's download checks (deno), or null if none is there yet.
    /// Without it YouTube downloads fail more often and some audio formats are missing.
    /// </summary>
    public string? DenoPath => File.Exists(ManagedDeno) ? ManagedDeno : File.Exists(SoundyDeno) ? SoundyDeno : null;

    /// <summary>Downloads yt-dlp.exe if no copy exists yet.</summary>
    public async Task EnsureInstalledAsync(CancellationToken ct)
    {
        await installLock.WaitAsync(ct);
        try
        {
            if (File.Exists(ExePath))
                return;

            if (!string.IsNullOrWhiteSpace(customPath()))
                throw new FileNotFoundException($"yt-dlp not found at configured path: {customPath()}");

            Plugin.Log.Information("Downloading yt-dlp...");
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tundy-Dalamud-Plugin");
            var bytes = await http.GetByteArrayAsync(DownloadUrl, ct);
            var tmp = ManagedExe + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, ManagedExe, true);
            Plugin.Log.Information("yt-dlp installed.");
        }
        finally
        {
            installLock.Release();
        }
    }

    /// <summary>Downloads deno (official release) if neither Tundy nor Soundy has a copy.</summary>
    public async Task EnsureDenoAsync(CancellationToken ct)
    {
        if (DenoPath != null) return;
        await denoLock.WaitAsync(ct);
        try
        {
            if (DenoPath != null) return;
            Plugin.Log.Information("Downloading deno (JavaScript runtime yt-dlp needs for YouTube)...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tundy-Dalamud-Plugin");
            var zip = await http.GetByteArrayAsync(DenoUrl, ct);
            using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("deno.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("deno.exe not found in the download.");
            var tmp = ManagedDeno + ".tmp";
            entry.ExtractToFile(tmp, true);
            File.Move(tmp, ManagedDeno, true);
            Plugin.Log.Information("deno installed.");
        }
        finally
        {
            denoLock.Release();
        }
    }

    public async Task<(int Exit, string Stdout, string Stderr)> UpdateAsync(CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var result = await RunAsync(new[] { "-U" }, ct);
        if (result.Exit != 0)
            Plugin.Log.Warning($"yt-dlp update failed: {result.Stderr}");
        else
            Plugin.Log.Information($"yt-dlp update: {result.Stdout.Trim().Split('\n').LastOrDefault()}");
        return result;
    }

    /// <summary>
    /// Downloads the audio of a single video into <paramref name="outDir"/> as "<paramref name="fileStem"/>.ext".
    /// Picks an m4a/AAC stream so no ffmpeg is required for playback via Media Foundation.
    /// </summary>
    public async Task<SongDownload> DownloadAudioAsync(string url, string outDir, int maxDurationSeconds, CancellationToken ct, string? fileStem = null)
    {
        await EnsureInstalledAsync(ct);
        Directory.CreateDirectory(outDir);

        var stem = string.IsNullOrWhiteSpace(fileStem) ? Guid.NewGuid().ToString("N") : fileStem;
        var template = Path.Combine(outDir, stem + ".%(ext)s");

        var args = new[]
        {
            "--no-playlist",
            "--no-progress",
            "--no-warnings",
            "--restrict-filenames",
            "--concurrent-fragments", "4", // download several pieces at once
            "--match-filter", $"duration <= {maxDurationSeconds} & !is_live",
            "-f", "bestaudio[ext=m4a]/bestaudio[acodec^=mp4a]/bestaudio",
            "-o", template,
            "--print", "after_move:%(title)s\t%(artist)s\t%(track)s",
            "--print", "after_move:filepath",
            "--", url,
        };

        var (exit, stdout, stderr) = await RunAsync(args, ct);
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (exit != 0 || lines.Length < 2)
        {
            var reason = stderr.Trim();
            if (string.IsNullOrEmpty(reason) && lines.Length == 0)
                reason = $"Video rejected (live stream, or longer than {maxDurationSeconds}s).";
            throw new InvalidOperationException(reason.Length > 300 ? reason[..300] : reason);
        }

        var file = lines[^1];
        if (!File.Exists(file))
            throw new FileNotFoundException("yt-dlp reported a file that does not exist.", file);

        var (title, artist, track) = ParseInfo(lines[^2]);
        return new SongDownload(file, title, artist, track);
    }

    /// <summary>A video's title and YouTube's artist/track fields, without downloading. Null if it can't be read.</summary>
    public async Task<SongDownload?> GetInfoAsync(string url, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var (exit, stdout, _) = await RunAsync(new[]
        {
            "--no-playlist", "--no-warnings", "--skip-download",
            "--print", "%(title)s\t%(artist)s\t%(track)s",
            "--", url,
        }, ct);
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (exit != 0 || string.IsNullOrWhiteSpace(line)) return null;
        var (title, artist, track) = ParseInfo(line);
        return new SongDownload("", title, artist, track);
    }

    private static (string Title, string? Artist, string? Track) ParseInfo(string line)
    {
        static string? Field(string s) => s is "NA" or "" ? null : s.Trim();
        var p = line.Split('\t');
        return p.Length >= 3 ? (p[0], Field(p[1]), Field(p[2])) : (line, null, null);
    }

    /// <summary>Quick YouTube search (no video pages opened): id, title, length and channel of the top results.</summary>
    public async Task<IReadOnlyList<YouTubeResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var (exit, stdout, stderr) = await RunAsync(new[]
        {
            "--flat-playlist", "--no-warnings", "--skip-download",
            // Description goes last: it can contain line breaks, and only its start matters ("Provided to YouTube by").
            "--print", "%(id)s\t%(duration)s\t%(channel)s\t%(view_count)s\t%(title)s\t%(description).100s",
            "--", $"ytsearch{Math.Clamp(count, 1, 20)}:{query}",
        }, ct);
        if (exit != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "YouTube search failed." : stderr.Trim());

        static string? Field(string s) => s is "NA" or "" ? null : s;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = new List<YouTubeResult>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('\t');
            if (p.Length < 6 || p[0].Length != 11) continue;
            int? seconds = double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var d) ? (int)Math.Round(d) : null;
            long? views = long.TryParse(p[3], System.Globalization.NumberStyles.Integer, inv, out var v) ? v : null;
            list.Add(new YouTubeResult(p[0], p[4], seconds, Field(p[2]), views, Field(string.Join(' ', p.Skip(5)))));
        }
        return list;
    }

    /// <summary>Lists a YouTube playlist without opening each video: its name, and each video's id, title and length.</summary>
    public async Task<(string? Name, IReadOnlyList<YouTubeResult> Entries)> ListPlaylistAsync(string url, int maxItems, CancellationToken ct)
    {
        await EnsureInstalledAsync(ct);
        var (exit, stdout, stderr) = await RunAsync(new[]
        {
            "--flat-playlist", "--no-warnings", "--skip-download", "--yes-playlist",
            "--playlist-end", Math.Clamp(maxItems, 1, 500).ToString(),
            "--print", "%(id)s\t%(duration)s\t%(playlist_title)s\t%(title)s",
            "--", url,
        }, ct);
        if (exit != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "Couldn't read that YouTube playlist." : stderr.Trim());

        string? name = null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var list = new List<YouTubeResult>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = line.Split('\t');
            if (p.Length < 4 || p[0].Length != 11) continue;
            int? seconds = double.TryParse(p[1], System.Globalization.NumberStyles.Float, inv, out var d) ? (int)Math.Round(d) : null;
            if (name == null && p[2] is not ("NA" or "")) name = p[2];
            list.Add(new YouTubeResult(p[0], string.Join('\t', p.Skip(3)), seconds, null));
        }
        if (list.Count == 0) throw new InvalidOperationException("That YouTube playlist is empty or private.");
        return (name, list);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ExePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // yt-dlp on Windows otherwise writes in the old cp1252 code page, which turns titles in other alphabets
        // (Japanese, Korean, Cyrillic...) into junk like "()".
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.ArgumentList.Add("--encoding");
        psi.ArgumentList.Add("utf-8");
        if (DenoPath is { } deno && args.FirstOrDefault() != "-U")
        {
            psi.ArgumentList.Add("--js-runtimes");
            psi.ArgumentList.Add($"deno:{deno}");
        }
        foreach (var a in args)
            psi.ArgumentList.Add(a); // ArgumentList escapes each argument: no shell injection from chat text.

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start yt-dlp.");
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { /* ignored */ }
            throw;
        }

        return (proc.ExitCode, await outTask, await errTask);
    }
}
