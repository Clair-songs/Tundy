using System;
using System.Collections.Generic;
using System.Globalization;

namespace Tundy;

/// <summary>
/// Loudness and EQ for imported songs, as ffmpeg filters. The preset names, their order (so Soundy's saved
/// choice keeps its meaning) and the five band gains in dB come from Soundy (kcuY, AGPL-3.0); the loudness
/// curve too (1.0 = original, up to 5.0, with a limiter so louder songs don't clip).
/// </summary>
public static class AudioPresets
{
    public static readonly string[] Names =
    {
        "Neutral", "Club / Bass", "EDM", "Rock", "Pop", "Orchestral / Score", "Vocal Boost", "Chill / Lo-Fi",
    };

    // Gains for the bands below, per preset.
    private static readonly float[][] Gains =
    {
        new[] { 0f, 0f, 0f, 0f, 0f },
        new[] { 4f, -1.5f, 0f, 0.5f, 1f },
        new[] { 3f, -1f, 0f, 1.5f, 1.5f },
        new[] { 2.5f, -1f, 0f, 2f, 2f },
        new[] { 2f, -1f, 1f, 2f, 2f },
        new[] { -1f, 0f, 0.5f, 1.5f, 2f },
        new[] { -1f, -1f, 3f, 2f, 1f },
        new[] { 0f, 2f, 0f, -1f, -2f },
    };

    private static readonly int[] Bands = { 80, 250, 1000, 4000, 12000 };

    /// <summary>Soundy's loudness setting (1-5) as a volume factor.</summary>
    public static float VolumeFactor(float setting) => 0.625f * Math.Clamp(setting, 1f, 5f) + 0.375f;

    /// <summary>The ffmpeg filter chain for a loudness setting and preset, or null if nothing changes.</summary>
    public static string? Filter(float loudness, int preset)
    {
        static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var parts = new List<string>();
        var gains = Gains[Math.Clamp(preset, 0, Gains.Length - 1)];
        for (var i = 0; i < Bands.Length; i++)
            if (Math.Abs(gains[i]) > 0.01f)
                parts.Add($"equalizer=f={Bands[i]}:t=o:w=1.5:g={N(gains[i])}");

        var factor = VolumeFactor(loudness);
        if (Math.Abs(factor - 1f) > 0.01f) parts.Add($"volume={N(factor)}");
        if (parts.Count > 0) parts.Add("alimiter=limit=0.95:attack=5:release=50"); // keep boosted songs from clipping
        return parts.Count > 0 ? string.Join(",", parts) : null;
    }
}
