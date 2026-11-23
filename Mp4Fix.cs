using System;
using System.IO;
using System.Text;

namespace Tundy;

/// <summary>
/// Makes YouTube's m4a downloads readable by Windows' own decoder (Media Foundation).
///
/// YouTube's m4a audio is a "fragmented" MP4. Some of these files carry an edit list (an "edts" box in the track
/// header) that trims the encoder's start-up padding (a few dozen milliseconds). Media Foundation reads such a
/// file as having no audio at all, so the song would neither play nor convert. Renaming the box to "free" (a box
/// every MP4 reader skips) fixes that; the only difference is that the tiny bit of padding plays.
/// </summary>
public static class Mp4Fix
{
    /// <summary>Turns every edit list in the file's track headers into a skipped box. Safe to call more than once.</summary>
    /// <returns>How many edit lists were removed.</returns>
    public static int StripEditLists(string file)
    {
        if (!file.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return 0;

        using var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var length = fs.Length;
        long pos = 0;
        while (pos + 8 <= length)
        {
            var (size, type, header) = ReadBoxHeader(fs, pos, length);
            if (size < 8) break;
            if (type == "moov")
            {
                var moov = new byte[size - header];
                fs.Position = pos + header;
                fs.ReadExactly(moov);
                var fixedCount = StripIn(moov, 0, moov.Length);
                if (fixedCount > 0)
                {
                    fs.Position = pos + header;
                    fs.Write(moov);
                }
                return fixedCount;
            }
            pos += size;
        }
        return 0;
    }

    private static (long Size, string Type, int Header) ReadBoxHeader(Stream s, long pos, long end)
    {
        var buf = new byte[16];
        s.Position = pos;
        if (s.Read(buf, 0, 8) < 8) return (0, "", 8);
        long size = ((long)buf[0] << 24) | ((long)buf[1] << 16) | ((long)buf[2] << 8) | buf[3];
        var type = Encoding.ASCII.GetString(buf, 4, 4);
        var header = 8;
        if (size == 1)
        {
            if (s.Read(buf, 8, 8) < 8) return (0, type, 16);
            size = 0;
            for (var i = 8; i < 16; i++) size = (size << 8) | buf[i];
            header = 16;
        }
        else if (size == 0)
        {
            size = end - pos; // box runs to the end of the file
        }
        return (size, type, header);
    }

    /// <summary>Walks the boxes in buf[start..end) and renames "edts" inside track boxes to "free".</summary>
    private static int StripIn(byte[] buf, int start, int end)
    {
        var count = 0;
        var pos = start;
        while (pos + 8 <= end)
        {
            var size = (buf[pos] << 24) | (buf[pos + 1] << 16) | (buf[pos + 2] << 8) | buf[pos + 3];
            if (size < 8 || pos + size > end) break;
            var type = Encoding.ASCII.GetString(buf, pos + 4, 4);
            if (type == "edts")
            {
                buf[pos + 4] = (byte)'f';
                buf[pos + 5] = (byte)'r';
                buf[pos + 6] = (byte)'e';
                buf[pos + 7] = (byte)'e';
                count++;
            }
            else if (type == "trak")
            {
                count += StripIn(buf, pos + 8, pos + size);
            }
            pos += size;
        }
        return count;
    }
}
