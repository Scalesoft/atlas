using System.Globalization;

namespace Atlas.Server.Platform.Validation;

/// <summary>Opaque compare-and-swap token for temporal administration rows: the ActiveFrom of the current interval, round-tripped as UTC.</summary>
public static class AdministrationVersion
{
    public static string Format(DateTime value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>For a row read back as a summary: <paramref name="formatted"/> is the <see cref="Format"/> string the server itself made
    /// of its ActiveFrom, never a client token. It is parsed, so anything else throws a FormatException instead of answering 409.</summary>
    public static void ExpectFormatted(string formatted, string? expected)
    {
        Expect(DateTime.Parse(formatted, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), expected);
    }

    public static void Expect(DateTime? actual, string? expected)
    {
        if (actual is null && expected is null)
        {
            return;
        }
        if (actual is null || expected is null || !string.Equals(Format(actual.Value), expected, StringComparison.Ordinal))
        {
            throw new ApiException(409, "Údaj se mezitím změnil. Obnovte seznam a změnu zopakujte.");
        }
    }
}
