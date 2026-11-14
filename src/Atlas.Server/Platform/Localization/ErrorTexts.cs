using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Localization;

/// <summary>
/// The one translation point of the texts the API answers people with: problem titles (<c>ProblemResponse</c>,
/// <c>RequestErrorHandling</c>). The Czech text is the source, the key and the fallback: its English is looked up by it in
/// <c>Platform/Localization/en/*.json</c> (embedded; one file per feature, so waves do not share a file), and a text with no
/// English answers in Czech. A text with values is a template whose <c>{name}</c> placeholders <see cref="Format"/> fills, in
/// either language (<c>ApiException</c>'s args). The machine <c>code</c> of a problem never changes with the language.
/// </summary>
public static partial class ErrorTexts
{
    private const string Prefix = "Atlas.Server.Platform.Localization.en.";

    private static readonly FrozenDictionary<string, string> English = Load();

    /// <summary>The text in the request's language; <paramref name="args"/> fill the template's placeholders.</summary>
    public static string Translate(string language, string czech, IReadOnlyDictionary<string, object?>? args = null)
    {
        var isEnglish = language == RequestLanguage.English;
        var text = isEnglish && English.TryGetValue(czech, out var english) ? english : czech;
        // A Czech label passed as a value (a field name) is translated as well when it has an English text.
        return args is null ? text : Format(text, isEnglish ? args.ToDictionary(x => x.Key, x => x.Value is string label && English.TryGetValue(label, out var translated) ? translated : x.Value) : args);
    }

    /// <summary>Fills <c>{name}</c> from <paramref name="args"/> (invariant culture); an unknown name stays as written.</summary>
    public static string Format(string template, IReadOnlyDictionary<string, object?> args)
        => Placeholder().Replace(template, match => args.TryGetValue(match.Groups[1].Value, out var value)
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
            : match.Value);

    /// <summary>Every English text by its Czech source, for the test that keeps each key in the code.</summary>
    public static IReadOnlyDictionary<string, string> EnglishTexts => English;

    private static FrozenDictionary<string, string> Load()
    {
        var assembly = typeof(ErrorTexts).Assembly;
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames().Where(x => x.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            foreach (var (czech, english) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!)
            {
                texts[czech] = english;
            }
        }
        return texts.ToFrozenDictionary(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Placeholder();
}
