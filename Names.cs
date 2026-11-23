using System;

namespace Tundy;

/// <summary>Character names as they show up in tells and in SoundyQ's old settings.</summary>
public static class Names
{
    /// <summary>
    /// "●Alex Example" / "Name @ World" -> a plain name to show. Also repairs names SoundyQ stored twice
    /// ("Alex ExampleAlex @ Example" -> "Alex Example").
    /// </summary>
    public static string Clean(string name)
    {
        var start = 0;
        while (start < name.Length && !char.IsLetterOrDigit(name[start])) start++;
        var trimmed = name[start..].Trim();

        // "<name><name>" split by SoundyQ into "<name+first part> @ <rest>": glue it back and halve it.
        var joined = trimmed.Replace(" @ ", " ", StringComparison.Ordinal);
        if (joined.Length % 2 == 0 && joined.Length >= 6)
        {
            var half = joined[..(joined.Length / 2)];
            if (joined == half + half) return half.Trim();
        }

        var at = trimmed.IndexOf(" @ ", StringComparison.Ordinal);
        return (at > 0 ? trimmed[..at] : trimmed).Trim();
    }

    /// <summary>
    /// Whether two character names are the same person, ignoring symbols in front ("●Name"), a world after
    /// "@" ("Name @ World", "Name@World"), extra spaces and capitalization.
    /// </summary>
    public static bool Same(string a, string b)
    {
        var na = Norm(a);
        return na.Length > 0 && na == Norm(b);
    }

    private static string Norm(string s)
    {
        s = Clean(s);
        var at = s.IndexOf('@');
        if (at > 0) s = s[..at];
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}
