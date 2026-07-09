using System.Text.RegularExpressions;

namespace DreamOfElectricStorage.Core;

/// <summary>
/// Normalizes a file name to a comparison stem so versioned/copied variants group together:
/// "report_v2.docx", "report (1).docx", "report - Copy.docx" → "report".
/// </summary>
public static partial class NameStem
{
    // Trailing decorations, innermost-last: " (3)", " - Copy", "_v12"/"-v2", " copy 2".
    [GeneratedRegex(@"( \(\d+\)|[-_ ]+copy( \d+)?|[-_ ]v\d+|[-_ ]\d{1,4})+$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingDecorations();

    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Strip extension (but not a leading dot-name like ".gitignore").
        int dot = name.LastIndexOf('.');
        string stem = dot > 0 ? name[..dot] : name;

        stem = TrailingDecorations().Replace(stem, "");
        return stem.Trim().ToLowerInvariant();
    }

    /// <summary>Two names are similar when their stems match and neither stem is trivially short.</summary>
    public static bool AreSimilar(string a, string b)
    {
        string stemA = Normalize(a);
        if (stemA.Length < 3)
            return false;
        return stemA == Normalize(b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
