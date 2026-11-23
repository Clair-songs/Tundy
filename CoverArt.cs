using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Tundy;

/// <summary>
/// Song covers: the YouTube video's thumbnail (from YouTube's own image server, i.ytimg.com), downloaded once
/// per song into Tundy's folder and shown in the player. Only the song that's playing (or next) is fetched.
/// </summary>
public sealed class CoverArt : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly ITextureProvider textures;
    private readonly string dir;
    private readonly ConcurrentDictionary<string, Task<string?>> files = new();

    public CoverArt(ITextureProvider textures, string dir)
    {
        this.textures = textures;
        this.dir = dir;
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignored */ }
        Directory.CreateDirectory(dir);
    }

    /// <summary>The cover for a YouTube video id, or null while it's loading or if there's none.</summary>
    public IDalamudTextureWrap? Get(string? videoId)
    {
        if (videoId == null || videoId.Length != 11) return null;
        var task = files.GetOrAdd(videoId, Download);
        if (!task.IsCompletedSuccessfully || task.Result is not { } file) return null;
        try { return textures.GetFromFile(file).GetWrapOrDefault(); }
        catch { return null; }
    }

    private async Task<string?> Download(string videoId)
    {
        try
        {
            // 320x180 "medium quality" thumbnail: small, and every video has one.
            var bytes = await Http.GetByteArrayAsync($"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg");
            var file = Path.Combine(dir, videoId + ".jpg");
            await File.WriteAllBytesAsync(file, bytes);
            return file;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, $"No cover for {videoId}");
            return null;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, true); } catch { /* ignored, the files may still be in use */ }
    }
}
