using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace Tundy;

/// <summary>
/// Evens out song volume: measures a song's loudness the way YouTube and Spotify do (ITU-R BS.1770 / EBU R128
/// integrated loudness, in LUFS) and works out how much to turn it up or down to reach the same level as the others.
/// Needs no ffmpeg: the song is decoded with Windows' own decoder, like for playback.
/// </summary>
public static class Loudness
{
    /// <summary>The level every song is brought to (YouTube's and Spotify's normal level).</summary>
    public const double TargetLufs = -14;

    /// <summary>Never turn a song up or down by more than this.</summary>
    private const double MaxBoostDb = 10, MaxCutDb = 15;

    /// <summary>A boosted song's loudest sample stays at or below this (-1 dBFS), so turning it up never clips.</summary>
    private const double PeakCeiling = 0.891;

    /// <summary>
    /// The gain in dB that brings this song to <see cref="TargetLufs"/>: negative turns it down, positive up (only as
    /// far as its peaks allow). 0 for silence or songs too short to measure.
    /// </summary>
    public static double GainDbFor(string audioFile, CancellationToken ct)
    {
        var (lufs, peak) = Measure(audioFile, ct);
        if (double.IsNaN(lufs) || double.IsInfinity(lufs)) return 0;
        var gain = Math.Clamp(TargetLufs - lufs, -MaxCutDb, MaxBoostDb);
        if (gain > 0 && peak > 0)
            gain = Math.Min(gain, Math.Max(0, 20 * Math.Log10(PeakCeiling / peak)));
        return Math.Round(gain, 1);
    }

    /// <summary>Integrated loudness (LUFS) and sample peak (0-1) of a song.</summary>
    public static (double Lufs, double Peak) Measure(string audioFile, CancellationToken ct)
    {
        using var reader = new MediaFoundationReader(audioFile);
        var sp = reader.ToSampleProvider();
        var rate = sp.WaveFormat.SampleRate;
        var channels = sp.WaveFormat.Channels;

        // K-weighting: a high shelf (the head's effect) then a high-pass (RLB), per channel.
        var shelf = new Biquad[channels];
        var highPass = new Biquad[channels];
        for (var c = 0; c < channels; c++)
        {
            shelf[c] = Biquad.HighShelf(rate);
            highPass[c] = Biquad.HighPass(rate);
        }

        // Mean square per 100 ms piece (all channels summed); 400 ms blocks are four pieces in a row.
        var pieceFrames = rate / 10;
        var pieces = new List<double>(4096);
        double sum = 0, peak = 0;
        var framesInPiece = 0;
        var buf = new float[channels * 8192];
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i + channels <= n; i += channels)
            {
                for (var c = 0; c < channels; c++)
                {
                    double x = buf[i + c];
                    var a = Math.Abs(x);
                    if (a > peak) peak = a;
                    var y = highPass[c].Next(shelf[c].Next(x));
                    sum += y * y;
                }
                if (++framesInPiece == pieceFrames)
                {
                    pieces.Add(sum / pieceFrames);
                    sum = 0;
                    framesInPiece = 0;
                }
            }
        }

        if (pieces.Count < 4) return (double.NaN, peak);
        var blocks = new List<double>(pieces.Count);
        for (var i = 0; i + 4 <= pieces.Count; i++)
            blocks.Add((pieces[i] + pieces[i + 1] + pieces[i + 2] + pieces[i + 3]) / 4);

        // Gating: leave out near-silence (below -70 LUFS), then everything 10 LU below the rest's average.
        static double Lufs(double meanSquare) => -0.691 + 10 * Math.Log10(meanSquare);
        var loud = blocks.FindAll(b => b > 0 && Lufs(b) > -70);
        if (loud.Count == 0) return (double.NaN, peak);
        var relative = Lufs(Average(loud)) - 10;
        var kept = loud.FindAll(b => Lufs(b) > relative);
        return (kept.Count == 0 ? double.NaN : Lufs(Average(kept)), peak);
    }

    private static double Average(List<double> values)
    {
        double s = 0;
        foreach (var v in values) s += v;
        return s / values.Count;
    }

    /// <summary>One second-order filter (the two stages of BS.1770's K-weighting, for any sample rate).</summary>
    private struct Biquad
    {
        private double b0, b1, b2, a1, a2, z1, z2;

        public static Biquad HighShelf(int rate)
        {
            const double gainDb = 3.999843853973347, q = 0.7071752369554196, fc = 1681.974450955533;
            var k = Math.Tan(Math.PI * fc / rate);
            var vh = Math.Pow(10, gainDb / 20);
            var vb = Math.Pow(vh, 0.4996667741545416);
            var a0 = 1 + k / q + k * k;
            return new Biquad
            {
                b0 = (vh + vb * k / q + k * k) / a0,
                b1 = 2 * (k * k - vh) / a0,
                b2 = (vh - vb * k / q + k * k) / a0,
                a1 = 2 * (k * k - 1) / a0,
                a2 = (1 - k / q + k * k) / a0,
            };
        }

        public static Biquad HighPass(int rate)
        {
            const double q = 0.5003270373238773, fc = 38.13547087602444;
            var k = Math.Tan(Math.PI * fc / rate);
            var a0 = 1 + k / q + k * k;
            return new Biquad
            {
                b0 = 1, b1 = -2, b2 = 1,
                a1 = 2 * (k * k - 1) / a0,
                a2 = (1 - k / q + k * k) / a0,
            };
        }

        public double Next(double x)
        {
            var y = b0 * x + z1;
            z1 = b1 * x - a1 * y + z2;
            z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
