using System;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Tundy;

/// <summary>
/// Turns a downloaded song into a game sound file (.scd) by taking an existing game .scd as a
/// template and swapping its audio for the song.
///
/// The song is stored as mono MS-ADPCM, the same format the game's own emote sounds use, so the
/// game treats it exactly like the original sound. Format notes follow VFXEditor's SCD code:
/// audio entries are stored last in the file, so everything before the first audio entry is kept
/// byte-for-byte, and an ADPCM entry is a 0x20 header, the WAV "fmt " chunk, then the ADPCM data.
/// </summary>
public static class ScdBuilder
{
    private const int FormatMsAdpcm = 0x0C;

    /// <summary>Encoded song ready to be put into an SCD.</summary>
    public sealed record Adpcm(byte[] FormatChunk, byte[] Data, int Channels, int SampleRate);

    /// <summary>Converts any audio file Media Foundation can read into mono MS-ADPCM.</summary>
    /// <param name="gain">Volume factor applied to the samples (1 = as it is), for evening out song volume.</param>
    public static Adpcm EncodeAdpcmMono(string audioFile, int sampleRate, CancellationToken ct, float gain = 1f)
    {
        // 1. Decode, mix to mono (game sound effects are mono so they can be placed around your
        //    character), and resample to the target rate as 16-bit PCM.
        short[] pcm;
        using (var reader = new MediaFoundationReader(audioFile))
        {
            ISampleProvider sp = reader.ToSampleProvider();
            if (sp.WaveFormat.Channels == 2)
                sp = new StereoToMonoSampleProvider(sp) { LeftVolume = 0.5f, RightVolume = 0.5f };
            else if (sp.WaveFormat.Channels != 1)
                throw new NotSupportedException($"Unsupported channel count {sp.WaveFormat.Channels}.");
            if (sp.WaveFormat.SampleRate != sampleRate)
                sp = new WdlResamplingSampleProvider(sp, sampleRate);

            var samples = new System.Collections.Generic.List<short>(sampleRate * 60 * 4);
            var buf = new float[16 * 1024];
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < n; i++)
                    samples.Add((short)Math.Clamp((int)MathF.Round(buf[i] * gain * 32767f), short.MinValue, short.MaxValue));
            }
            pcm = samples.ToArray();
        }

        if (pcm.Length == 0)
            throw new InvalidDataException("The song has no audio.");

        // 2. Encode to MS-ADPCM with the same block size the game uses.
        ct.ThrowIfCancellationRequested();
        var data = MsAdpcmEncoder.Encode(pcm);
        return new Adpcm(MsAdpcmEncoder.FormatChunk(sampleRate), data, 1, sampleRate);
    }

    /// <summary>Builds a new .scd from <paramref name="template"/> with all its audio replaced by <paramref name="audio"/>.</summary>
    public static byte[] Build(byte[] template, Adpcm audio)
    {
        if (template.Length < 0x50 || System.Text.Encoding.ASCII.GetString(template, 0, 8) != "SEDBSSCF")
            throw new InvalidDataException("Template is not an SCD file.");

        var soundCount = BitConverter.ToInt16(template, 0x30);
        var trackCount = BitConverter.ToInt16(template, 0x32);
        var audioCount = BitConverter.ToInt16(template, 0x34);
        var trackTable = BitConverter.ToInt32(template, 0x38);
        var audioTable = BitConverter.ToInt32(template, 0x3C);
        if (audioCount <= 0)
            throw new InvalidDataException("Template has no audio.");

        var audioOffsets = Enumerable.Range(0, audioCount).Select(i => BitConverter.ToInt32(template, audioTable + i * 4)).ToArray();
        var firstAudio = audioOffsets.Where(o => o > 0).Min();

        // Sanity check: sound and track data must come before the audio, or we'd cut it off.
        const int soundTable = 0x50;
        for (var i = 0; i < soundCount; i++)
            if (BitConverter.ToInt32(template, soundTable + i * 4) >= firstAudio)
                throw new InvalidDataException("Unexpected SCD layout.");
        for (var i = 0; i < trackCount; i++)
            if (BitConverter.ToInt32(template, trackTable + i * 4) >= firstAudio)
                throw new InvalidDataException("Unexpected SCD layout.");

        var oldFlags = BitConverter.ToInt32(template, firstAudio + 0x1C);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(template, 0, firstAudio);

        // Audio entry header (0x20)
        w.Write(audio.Data.Length);           // data length
        w.Write(audio.Channels);
        w.Write(audio.SampleRate);
        w.Write(FormatMsAdpcm);
        w.Write(0);                           // loop start (no loop)
        w.Write(0);                           // loop end
        w.Write(audio.FormatChunk.Length);    // sub-info size = the WAV format header
        w.Write(oldFlags & ~0x1);             // keep flags, drop the loop-marker flag
        w.Write(audio.FormatChunk);
        w.Write(audio.Data);
        while (ms.Length % 16 != 0) w.Write((byte)0);

        // Point every audio slot at the new entry (some sounds pick a random slot).
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
