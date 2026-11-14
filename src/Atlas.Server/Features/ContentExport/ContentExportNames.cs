using System.Text;

namespace Atlas.Server.Features.ContentExport;

/// <summary>Export file names from the document's own title, portable on every file system the file may be saved to.</summary>
public static class ContentExportNames
{
    public const int MaximumStemLength = 100;

    /// <summary>
    /// The title with characters no file system accepts replaced, whitespace collapsed and at most
    /// <see cref="MaximumStemLength"/> characters; <paramref name="fallback"/> when nothing usable is left.
    /// </summary>
    public static string Stem(string? title, string fallback)
    {
        var builder = new StringBuilder();
        foreach (var c in (title ?? "").Normalize(NormalizationForm.FormC))
        {
            // "Návod: krok/A?" reads as "Návod - krok-A".
            var replaced = char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? " "
                : c switch { ':' => " - ", '/' or '\\' or '|' => "-", '"' => "'", '<' or '>' or '?' or '*' => "", _ => c.ToString() };
            foreach (var part in replaced)
            {
                if (part == ' ' && (builder.Length == 0 || builder[^1] == ' ')) continue;
                builder.Append(part);
            }
        }
        var stem = builder.ToString();
        if (stem.Length > MaximumStemLength) stem = stem[..(char.IsHighSurrogate(stem[MaximumStemLength - 1]) ? MaximumStemLength - 1 : MaximumStemLength)];
        // Windows drops trailing dots and spaces; a stem of dots alone would name nothing.
        stem = stem.TrimEnd('.', ' ', '-').TrimStart('.', ' ');
        var device = stem.Split('.')[0].ToUpperInvariant();
        if (stem.Length == 0 || device is "CON" or "PRN" or "AUX" or "NUL"
            || device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(device[3]))
            return fallback;
        return stem;
    }
}
