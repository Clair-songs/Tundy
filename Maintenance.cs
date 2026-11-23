using System;
using System.IO;
using System.Linq;

namespace Tundy;

/// <summary>Clean-up tools (Soundy's "Clear Temp Files", plus leftovers from SoundyQ and Tundy itself).</summary>
public static class Maintenance
{
    /// <summary>Tundy's temporary files: unfinished conversions and downloads in the Windows temp folder.</summary>
    public static (int Files, long Bytes) ClearTempFiles()
    {
        int files = 0;
        long bytes = 0;
        var temp = new DirectoryInfo(Path.GetTempPath());
        foreach (var f in temp.GetFiles("tundy_*"))
            Delete(f, ref files, ref bytes);
        var imports = new DirectoryInfo(Path.Combine(temp.FullName, "tundy-import"));
        if (imports.Exists)
            foreach (var f in imports.GetFiles())
                Delete(f, ref files, ref bytes);
        return (files, bytes);
    }

    /// <summary>
    /// Songs SoundyQ left in the music mod ("soundy\soundyq" inside the mod's folder). Only that folder is touched.
    /// </summary>
    public static (int Files, long Bytes) ClearSoundyQLeftovers(string? musicModFolder)
    {
        int files = 0;
        long bytes = 0;
        if (musicModFolder == null) return (0, 0);
        var dir = new DirectoryInfo(Path.Combine(musicModFolder, "soundy", "soundyq"));
        if (!dir.Exists) return (0, 0);
        foreach (var f in dir.GetFiles("*.scd"))
            Delete(f, ref files, ref bytes);
        return (files, bytes);
    }

    /// <summary>Size of SoundyQ's leftovers, for the button label.</summary>
    public static long SoundyQLeftoverBytes(string? musicModFolder)
    {
        if (musicModFolder == null) return 0;
        var dir = new DirectoryInfo(Path.Combine(musicModFolder, "soundy", "soundyq"));
        return dir.Exists ? dir.GetFiles("*.scd").Sum(f => f.Length) : 0;
    }

    public static string Describe((int Files, long Bytes) r) =>
        r.Files == 0 ? "Nothing to clean." : $"Deleted {r.Files} file(s), {r.Bytes / 1048576.0:F1} MB.";

    private static void Delete(FileInfo f, ref int files, ref long bytes)
    {
        try
        {
            var size = f.Length;
            f.Delete();
            files++;
            bytes += size;
        }
        catch { /* in use; next time */ }
    }
}
