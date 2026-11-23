using System;
using System.IO;

namespace Tundy;

/// <summary>
/// Minimal mono MS-ADPCM encoder (WAVE_FORMAT_ADPCM, tag 2). Written here instead of using the
/// Windows codec so the block size can match the game's own sound files (128 samples per block).
/// </summary>
public static class MsAdpcmEncoder
{
    private static readonly int[] Coef1 = { 256, 512, 0, 192, 240, 460, 392 };
    private static readonly int[] Coef2 = { 0, -256, 0, 64, 0, -208, -232 };
    private static readonly int[] Adaptation = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };
    private static readonly int[] StartDeltas = { 16, 48, 128, 384, 1024, 3072 };

    public const int SamplesPerBlock = 128;
    public const int BlockAlign = (SamplesPerBlock - 2) / 2 + 7; // 70 bytes, same as the game

    /// <summary>The 50-byte WAVEFORMATEX + ADPCM extension ("fmt " chunk body).</summary>
    public static byte[] FormatChunk(int sampleRate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)2);                                        // WAVE_FORMAT_ADPCM
        w.Write((short)1);                                        // channels
        w.Write(sampleRate);
        w.Write((int)((long)sampleRate * BlockAlign / SamplesPerBlock)); // avg bytes/sec
        w.Write((short)BlockAlign);
        w.Write((short)4);                                        // bits per sample
        w.Write((short)32);                                       // extra size
        w.Write((short)SamplesPerBlock);
        w.Write((short)Coef1.Length);
        for (var i = 0; i < Coef1.Length; i++)
        {
            w.Write((short)Coef1[i]);
            w.Write((short)Coef2[i]);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Encodes 16-bit mono samples. The last block is padded with silence.</summary>
    public static byte[] Encode(short[] samples)
    {
        var blocks = (samples.Length + SamplesPerBlock - 1) / SamplesPerBlock;
        var output = new byte[blocks * BlockAlign];
        var block = new short[SamplesPerBlock];

        for (var b = 0; b < blocks; b++)
        {
            var start = b * SamplesPerBlock;
            var count = Math.Min(SamplesPerBlock, samples.Length - start);
            Array.Clear(block);
            Array.Copy(samples, start, block, 0, count);

            // Try each predictor and a few starting step sizes; keep whichever is closest to the original.
            long bestErr = long.MaxValue;
            int bestPred = 0, bestDelta = 16;
            for (var p = 0; p < Coef1.Length; p++)
            foreach (var d in StartDeltas)
            {
                var err = EncodeBlock(block, p, d, null);
                if (err < bestErr) { bestErr = err; bestPred = p; bestDelta = d; }
            }

            EncodeBlock(block, bestPred, bestDelta, output.AsSpan(b * BlockAlign, BlockAlign));
        }

        return output;
    }

    /// <summary>Encodes one block; returns the squared error. Writes bytes when <paramref name="dest"/> is given.</summary>
    private static long EncodeBlock(short[] s, int pred, int delta, Span<byte> dest)
    {
        int c1 = Coef1[pred], c2 = Coef2[pred];
        int sample2 = s[0], sample1 = s[1]; // header: first two samples stored as-is (older one first in time)
        var write = !dest.IsEmpty;

        if (write)
        {
            dest[0] = (byte)pred;
            WriteShort(dest, 1, (short)delta);
            WriteShort(dest, 3, (short)sample1);
            WriteShort(dest, 5, (short)sample2);
        }

        long err = 0;
        var pos = 7;
        var high = true;
        for (var i = 2; i < s.Length; i++)
        {
            var predicted = (sample1 * c1 + sample2 * c2) >> 8;
            var diff = s[i] - predicted;
            var bias = delta / 2;
            var nibble = (diff + (diff >= 0 ? bias : -bias)) / delta;
            nibble = Math.Clamp(nibble, -8, 7);

            var decoded = Math.Clamp(predicted + nibble * delta, short.MinValue, short.MaxValue);
            var e = s[i] - decoded;
            err += (long)e * e;

            sample2 = sample1;
            sample1 = decoded;
            var n = nibble & 0xF;
            delta = Math.Max(16, Adaptation[n] * delta >> 8);

            if (write)
            {
                if (high) dest[pos] = (byte)(n << 4);
                else dest[pos++] |= (byte)n;
            }
            high = !high;
        }

        return err;
    }

    private static void WriteShort(Span<byte> d, int at, short v)
    {
        d[at] = (byte)v;
        d[at + 1] = (byte)(v >> 8);
    }
}
