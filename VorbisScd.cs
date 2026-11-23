using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace Tundy;

/// <summary>
/// Builds music-mod song files (.scd) the way the Mega Music Mod's own songs are made: stereo Ogg Vorbis, encoded
/// with ffmpeg, inside a copy of one of the mod's tracks. Those tracks are set up as streamed, looping music, which
/// plays Vorbis; the mono ADPCM used for emote sounds loads there but stays silent.
///
/// Layout of a Vorbis audio entry (matches the mod's files, except that a song's loop covers only the silence added
/// at its end): the usual 0x20 entry header (format 6), then a 0x20 Vorbis header (no encryption, seek step in seconds, seek table size, header size 0),
/// then the seek table (byte offset 0, then the offset of every audio page but the last), then the plain Ogg stream.
/// </summary>
public static class VorbisScd
{
    private const int FormatVorbis = 0x06;

    /// <summary>ffmpeg to use: Tundy's own copy if there is one, otherwise the one Soundy downloaded.</summary>
    public static string? FindFfmpeg(string tundyToolDir)
    {
        var own = Path.Combine(tundyToolDir, "ffmpeg.exe");
        if (File.Exists(own)) return own;
        var configs = Directory.GetParent(Directory.GetParent(tundyToolDir)!.FullName)!.FullName; // ...\pluginConfigs
        var soundy = Path.Combine(configs, "Soundy", "tools", "ffmpeg.exe");
        return File.Exists(soundy) ? soundy : null;
    }

    /// <summary>Seconds of silence added after each song; the loop stays inside it (see <see cref="Build"/>).</summary>
    public const double SilentTail = 4;

    private static string Num(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Encodes any audio file to 44.1 kHz stereo Ogg Vorbis with ffmpeg.</summary>
    /// <param name="quality">libvorbis quality, 0-10 (6 is about 190 kbit/s).</param>
    /// <param name="filter">Optional ffmpeg audio filters (loudness, EQ), see <see cref="AudioPresets"/>.</param>
    /// <param name="silentTail">Add <see cref="SilentTail"/> of silence at the end (for requested songs).</param>
    public static byte[] EncodeOgg(string ffmpeg, string audioFile, int quality, CancellationToken ct, string? filter = null, bool silentTail = true)
    {
        var filters = new[] { filter, silentTail ? $"apad=pad_dur={Num(SilentTail)}" : null }.Where(f => !string.IsNullOrEmpty(f)).ToList();
        var input = new List<string> { "-i", audioFile };
        if (filters.Count > 0) input.AddRange(new[] { "-af", string.Join(",", filters) });
        return ReadAndDelete(RunFfmpeg(ffmpeg, input.ToArray(), new[] { "-ac", "2", "-ar", "44100", "-c:a", "libvorbis", "-q:a", Math.Clamp(quality, 0, 10).ToString() }, ".ogg", ct));
    }

    /// <summary>A short stretch of silence as Ogg Vorbis, for the song slot between songs.</summary>
    public static byte[] EncodeSilence(string ffmpeg, double seconds, CancellationToken ct) =>
        ReadAndDelete(RunFfmpeg(ffmpeg, new[] { "-f", "lavfi", "-i", "anullsrc=r=44100:cl=stereo", "-t", Num(seconds) },
            new[] { "-ac", "2", "-ar", "44100", "-c:a", "libvorbis", "-q:a", "0" }, ".ogg", ct));

    /// <summary>
    /// Converts any audio file to a 16-bit WAV (with optional filters) for the mono ADPCM sound effects, and returns
    /// the temporary file's path; the caller deletes it.
    /// </summary>
    public static string EncodeWav(string ffmpeg, string audioFile, string? filter, CancellationToken ct)
    {
        var input = new List<string> { "-i", audioFile };
        if (!string.IsNullOrEmpty(filter)) input.AddRange(new[] { "-af", filter });
        return RunFfmpeg(ffmpeg, input.ToArray(), new[] { "-ac", "2", "-ar", "44100", "-c:a", "pcm_s16le" }, ".wav", ct);
    }

    private static byte[] ReadAndDelete(string file)
    {
        try { return File.ReadAllBytes(file); }
        finally { try { File.Delete(file); } catch { /* ignored */ } }
    }

    /// <returns>The output file in the temp folder; the caller deletes it.</returns>
    private static string RunFfmpeg(string ffmpeg, string[] input, string[] codec, string extension, CancellationToken ct)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"tundy_{Guid.NewGuid():N}{extension}");
        var ok = false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-y", "-nostdin", "-hide_banner", "-v", "error" }
                         .Concat(input)
                         .Concat(new[] { "-vn", "-map_metadata", "-1" })
                         .Concat(codec)
                         .Append(outFile))
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start ffmpeg.");
            var stderr = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();
            while (!proc.WaitForExit(200))
            {
                if (!ct.IsCancellationRequested) continue;
                try { proc.Kill(); } catch { /* already gone */ }
                ct.ThrowIfCancellationRequested();
            }
            if (proc.ExitCode != 0 || !File.Exists(outFile))
                throw new InvalidOperationException($"ffmpeg couldn't convert the song: {stderr.Result.Trim()}");
            ok = true;
            return outFile;
        }
        finally
        {
            if (!ok) try { File.Delete(outFile); } catch { /* ignored */ }
        }
    }

    private sealed record OggPage(int Offset, int Size, long Granule);

    private static List<OggPage> Pages(byte[] ogg)
    {
        var pages = new List<OggPage>();
        var p = 0;
        while (p + 27 <= ogg.Length && ogg[p] == 'O' && ogg[p + 1] == 'g' && ogg[p + 2] == 'g' && ogg[p + 3] == 'S')
        {
            int segments = ogg[p + 26];
            if (p + 27 + segments > ogg.Length) break;
            var body = 0;
            for (var i = 0; i < segments; i++) body += ogg[p + 27 + i];
            var size = 27 + segments + body;
            if (p + size > ogg.Length) break;
            pages.Add(new OggPage(p, size, BitConverter.ToInt64(ogg, p + 6)));
            p += size;
        }
        if (pages.Count < 3 || p != ogg.Length)
            throw new InvalidDataException("ffmpeg's output isn't a complete Ogg Vorbis file.");
        return pages;
    }

    /// <summary>Builds a new .scd from <paramref name="template"/> (a music track) with its audio replaced by <paramref name="ogg"/>.</summary>
    /// <param name="loopSilentTail">
    /// The song slot only plays looping sounds (with the loop turned off it stays silent), and a loop over the whole
    /// song plays it again and again. So for songs, the loop covers only the silence at the end
    /// (<see cref="SilentTail"/>): the song plays once, then the game keeps looping silence. False loops everything.
    /// </param>
    public static byte[] Build(byte[] template, byte[] ogg, bool loopSilentTail = false)
    {
        if (template.Length < 0x50 || System.Text.Encoding.ASCII.GetString(template, 0, 8) != "SEDBSSCF")
            throw new InvalidDataException("Template is not an SCD file.");

        var soundCount = BitConverter.ToInt16(template, 0x30);
        var trackCount = BitConverter.ToInt16(template, 0x32);
        var audioCount = BitConverter.ToInt16(template, 0x34);
        var trackTable = BitConverter.ToInt32(template, 0x38);
        var audioTable = BitConverter.ToInt32(template, 0x3C);
        if (audioCount <= 0) throw new InvalidDataException("Template has no audio.");

        var audioOffsets = Enumerable.Range(0, audioCount).Select(i => BitConverter.ToInt32(template, audioTable + i * 4)).ToArray();
        var firstAudio = audioOffsets.Where(o => o > 0).Min();
        const int soundTable = 0x50;
        for (var i = 0; i < soundCount; i++)
            if (BitConverter.ToInt32(template, soundTable + i * 4) >= firstAudio) throw new InvalidDataException("Unexpected SCD layout.");
        for (var i = 0; i < trackCount; i++)
            if (BitConverter.ToInt32(template, trackTable + i * 4) >= firstAudio) throw new InvalidDataException("Unexpected SCD layout.");

        // Stream details from the Vorbis identification header (first page, after "\x01vorbis" + version).
        var pages = Pages(ogg);
        var id = pages[0].Offset + 27 + ogg[pages[0].Offset + 26];
        if (ogg[id] != 1 || System.Text.Encoding.ASCII.GetString(ogg, id + 1, 6) != "vorbis")
            throw new InvalidDataException("ffmpeg's output isn't Vorbis.");
        int channels = ogg[id + 11];
        var rate = BitConverter.ToInt32(ogg, id + 12);

        // Seek table: 0, then every audio page but the last. The first pages hold the three Vorbis headers;
        // audio pages are the ones with a real position.
        var audioPages = pages.Where(pg => pg.Granule > 0).ToList();
        var seek = new List<int> { 0 };
        seek.AddRange(audioPages.Take(Math.Max(0, audioPages.Count - 1)).Select(pg => pg.Offset));
        var totalSeconds = audioPages.Count > 0 ? audioPages[^1].Granule / (double)rate : 0;
        var seekStep = seek.Count > 1 ? (float)(totalSeconds / (seek.Count - 1)) : 1f;
        var subInfo = 0x20 + seek.Count * 4;

        // Loop start: the first page that begins well inside the silent ending (a page start, like every seek
        // entry). Pages hold about a second each, so the loop covers the last few seconds of silence.
        var loopStart = 0;
        if (loopSilentTail && audioPages.Count > 3)
        {
            var silenceFrom = audioPages[^1].Granule - (long)((SilentTail - 0.5) * rate);
            for (var i = 1; i < audioPages.Count - 1; i++)
            {
                if (audioPages[i - 1].Granule < silenceFrom) continue; // this page starts before the silence
                loopStart = audioPages[i].Offset;
                break;
            }
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(template, 0, firstAudio);

        // Audio entry header (0x20)
        w.Write(ogg.Length);       // data length
        w.Write(channels);
        w.Write(rate);
        w.Write(FormatVorbis);
        w.Write(loopStart);        // loop start (bytes into the Ogg data)
        w.Write(ogg.Length);       // loop end: the end of the data, like the music mod's own tracks
        w.Write(subInfo);          // everything between this header and the Ogg data
        w.Write(0);                // no extra chunks

        // Vorbis header (0x20)
        w.Write((short)0);         // no encryption
        w.Write((short)0);
        w.Write(0);                // xor offset
        w.Write(0);                // xor size
        w.Write(seekStep);
        w.Write(seek.Count * 4);   // seek table size
        w.Write(0);                // Vorbis headers are inside the Ogg data
        w.Write(0);
        w.Write(0);
        foreach (var s in seek) w.Write(s);

        w.Write(ogg);
        while (ms.Length % 16 != 0) w.Write((byte)0);

        for (var i = 0; i < audioCount; i++)
        {
            if (audioOffsets[i] <= 0) continue;
            ms.Position = audioTable + i * 4;
            w.Write(firstAudio);
        }
        ms.Position = 0x10; // file size
        w.Write((int)ms.Length);
        w.Flush();
        return ms.ToArray();
    }
}
