using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Tundy;

/// <summary>
/// Tundy's own copy of ffmpeg (needed for music mod songs and the importer). Downloaded once from the official
/// Windows builds at gyan.dev ("essentials", about 120 MB), checked against the published SHA-256, and only
/// ffmpeg.exe is kept. Until then the copy Soundy downloaded is used, if there is one.
/// </summary>
public sealed class FfmpegTool
{
    private const string ZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const string ShaUrl = ZipUrl + ".sha256";

    private readonly string toolDir;
    private readonly SemaphoreSlim downloadLock = new(1, 1);

    public FfmpegTool(string toolDir)
    {
        this.toolDir = toolDir;
        Directory.CreateDirectory(toolDir);
    }

    public string OwnPath => Path.Combine(toolDir, "ffmpeg.exe");

    /// <summary>The ffmpeg that will be used, or null if there's none yet.</summary>
    public string? CurrentPath => VorbisScd.FindFfmpeg(toolDir);

    public bool UsingSoundysCopy => CurrentPath is { } p && !p.Equals(OwnPath, StringComparison.OrdinalIgnoreCase);

    public bool Downloading { get; private set; }

    public string Status { get; private set; } = "";

    /// <summary>ffmpeg's path, downloading Tundy's own copy first if there's none at all. Any thread.</summary>
    public string Get(CancellationToken ct)
    {
        if (CurrentPath is { } p) return p;
        DownloadAsync(ct).GetAwaiter().GetResult();
        return CurrentPath ?? throw new FileNotFoundException("ffmpeg couldn't be installed. Try again in Settings → Download tools.");
    }

    /// <summary>Downloads (or replaces) Tundy's own ffmpeg.exe.</summary>
    public async Task DownloadAsync(CancellationToken ct)
    {
        await downloadLock.WaitAsync(ct);
        var zipFile = Path.Combine(toolDir, "ffmpeg-download.zip");
        try
        {
            Downloading = true;
            Status = "Downloading ffmpeg...";
            Plugin.Log.Information($"Downloading ffmpeg from {ZipUrl}");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Tundy-Dalamud-Plugin");

            var expected = (await http.GetStringAsync(ShaUrl, ct)).Trim().Split(' ', '\t', '\n')[0].ToLowerInvariant();
            if (expected.Length != 64) throw new InvalidDataException("The ffmpeg checksum couldn't be read.");

            using (var response = await http.GetAsync(ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? 0;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(zipFile);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) Status = $"Downloading ffmpeg... {done * 100 / total}% of {total / 1048576} MB";
                }
            }

            Status = "Checking the download...";
            string actual;
            await using (var fs = File.OpenRead(zipFile))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
            if (actual != expected)
                throw new InvalidDataException("The ffmpeg download doesn't match its published checksum, so it wasn't installed.");

            using (var archive = ZipFile.OpenRead(zipFile))
            {
                var entry = archive.Entries.FirstOrDefault(e =>
                                e.FullName.EndsWith("/bin/ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException("ffmpeg.exe wasn't in the download.");
                var tmp = OwnPath + ".tmp";
                entry.ExtractToFile(tmp, true);
                File.Move(tmp, OwnPath, true);
            }

            if (!HasVorbis(OwnPath))
                throw new InvalidDataException("This ffmpeg can't write Vorbis audio.");
            Status = "ffmpeg is installed.";
            Plugin.Log.Information("ffmpeg installed.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = $"ffmpeg download failed: {ex.Message}";
            Plugin.Log.Warning(ex, "ffmpeg download failed");
            throw;
        }
        finally
        {
            Downloading = false;
            try { File.Delete(zipFile); } catch { /* ignored */ }
            downloadLock.Release();
        }
    }

    private static bool HasVorbis(string ffmpeg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -encoders")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p == null) return false;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output.Contains("libvorbis", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
