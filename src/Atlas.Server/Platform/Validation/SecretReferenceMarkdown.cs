using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas.Server.Platform.Validation;

/// <summary>
/// The Markdown form of password references (editor 0.6.0, secrets plugin README "Markdown form"): <c>::secret{…}</c> alone on a
/// line is a block reference, <c>:secret{…}</c> in text an inline one. The directive carries only the reference attributes
/// (<see cref="SecretReferenceContent.Attributes"/>) as <c>name=value</c> pairs, never a value; the braces close on the same line
/// within 512 characters. Code (fenced blocks, code spans) is text. The rules mirror the plugin's parser: a directive with an
/// unknown, repeated or invalid attribute is refused as a whole, and nothing is dropped silently.
/// <para>
/// Stored Markdown is a projection of the rich JSON, so it may name only references the JSON holds; every reader-facing copy
/// (API, history, export, search) filters it like the JSON (<see cref="Strip"/>).
/// </para>
/// </summary>
public static partial class SecretReferenceMarkdown
{
    private const int MaxBodyLength = 512;

    /// <summary>Cheap check before scanning: false only when no directive can be inside.</summary>
    public static bool MayContainDirectives(string? markdown) => markdown is not null && markdown.Contains("secret{", StringComparison.Ordinal);

    /// <summary>The distinct secret ids the directives of <paramref name="markdown"/> name.</summary>
    /// <exception cref="InvalidDataException">A directive breaks the contract; the message never echoes the input.</exception>
    public static IReadOnlySet<Guid> Collect(string? markdown)
    {
        var ids = new HashSet<Guid>();
        foreach (var directive in Directives(markdown))
        {
            ids.Add(directive.SecretId ?? throw Invalid());
        }
        return ids;
    }

    /// <summary>
    /// The save rule: every directive keeps the contract and names a reference <paramref name="documentIds"/> (the rich JSON of the
    /// same request) holds, so the JSON checks (Discover for the author, no overwriting of hidden references) cover the Markdown.
    /// </summary>
    /// <exception cref="InvalidDataException">A directive breaks the contract or names a reference the JSON does not hold.</exception>
    public static void RequireWithin(string? markdown, IReadOnlySet<Guid> documentIds)
    {
        if (!Collect(markdown).IsSubsetOf(documentIds))
        {
            throw Invalid();
        }
    }

    /// <summary>
    /// A copy without the directives <paramref name="visible"/> refuses; a directive that breaks the contract always goes (fail
    /// closed). The input is returned unchanged when nothing is removed.
    /// </summary>
    public static string Strip(string markdown, Func<Guid, bool> visible, out int removed)
    {
        removed = 0;
        var directives = Directives(markdown).Where(d => d.SecretId is not { } id || !visible(id)).ToArray();
        if (directives.Length == 0)
        {
            return markdown;
        }
        removed = directives.Length;
        var result = new StringBuilder(markdown.Length);
        var position = 0;
        foreach (var directive in directives)
        {
            result.Append(markdown, position, directive.Start - position);
            position = directive.Start + directive.Length;
        }
        return result.Append(markdown, position, markdown.Length - position).ToString();
    }

    /// <summary>Every directive outside code, in document order.</summary>
    public static IEnumerable<SecretDirective> Directives(string? markdown)
    {
        if (!MayContainDirectives(markdown))
        {
            yield break;
        }
        string? fence = null;
        var lineStart = 0;
        while (lineStart <= markdown!.Length)
        {
            var lineEnd = markdown.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = markdown.Length;
            }
            var line = markdown.AsSpan(lineStart, lineEnd - lineStart);
            var marker = FenceMarker(line);
            if (fence is not null)
            {
                if (marker is not null && marker[0] == fence[0] && marker.Length >= fence.Length && IsBlank(line[(line.IndexOf(marker) + marker.Length)..]))
                {
                    fence = null;
                }
            }
            else if (marker is not null)
            {
                fence = marker;
            }
            else if (line.Contains("secret{", StringComparison.Ordinal))
            {
                foreach (var directive in LineDirectives(markdown, lineStart, lineEnd))
                {
                    yield return directive;
                }
            }
            lineStart = lineEnd + 1;
        }
    }

    private static IEnumerable<SecretDirective> LineDirectives(string markdown, int start, int end)
    {
        var code = CodeSpans(markdown, start, end);
        foreach (Match match in Opener().Matches(markdown[start..end]))
        {
            var at = start + match.Index;
            if (code.Any(span => at >= span.Start && at < span.End))
            {
                continue;
            }
            var bodyStart = at + match.Length;
            var close = -1;
            for (var position = bodyStart; position < end && position <= bodyStart + MaxBodyLength; position++)
            {
                var c = markdown[position];
                if (c == '}')
                {
                    close = position;
                    break;
                }
                if (c == '{')
                {
                    break;
                }
            }
            if (close < 0)
            {
                // Braces that do not close on the line are text for the editor too.
                continue;
            }
            var body = markdown.AsSpan(bodyStart, close - bodyStart);
            yield return new SecretDirective(at, close + 1 - at, match.Length == "::secret{".Length, Parse(body));
        }
    }

    /// <summary>The secret id of a valid attribute list, or null when it breaks the contract.</summary>
    private static Guid? Parse(ReadOnlySpan<char> body)
    {
        var text = body.ToString();
        var attributes = new List<KeyValuePair<string, JsonElement>>();
        var position = 0;
        while (position < text.Length)
        {
            if (string.IsNullOrWhiteSpace(text[position..]))
            {
                break;
            }
            var match = Attribute().Match(text, position);
            if (!match.Success || match.Index != position)
            {
                return null;
            }
            var name = match.Groups[1].Value;
            var value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            attributes.Add(KeyValuePair.Create(name, Typed(name, value)));
            position = match.Index + match.Length;
        }
        return SecretReferenceContent.TryValidateAttributes(attributes, true, out var secretId) ? secretId : null;
    }

    /// <summary>The plugin's reading of one value: <c>fields</c> a space-separated list, booleans, a number or <c>null</c>.</summary>
    private static JsonElement Typed(string name, string text) => name switch
    {
        "fields" => JsonSerializer.SerializeToElement(text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)),
        "showCopy" or "showDetails" when text is "true" or "false" => JsonSerializer.SerializeToElement(text == "true"),
        "autoHideSeconds" when text == "null" => JsonSerializer.SerializeToElement<object?>(null),
        "autoHideSeconds" when Digits().IsMatch(text) => JsonSerializer.SerializeToElement(int.Parse(text)),
        _ => JsonSerializer.SerializeToElement(text)
    };

    /// <summary>Code spans of one line: a run of n backticks up to the next run of exactly n.</summary>
    private static List<(int Start, int End)> CodeSpans(string markdown, int start, int end)
    {
        var spans = new List<(int Start, int End)>();
        var position = start;
        while (position < end)
        {
            var open = markdown.IndexOf('`', position, end - position);
            if (open < 0)
            {
                break;
            }
            var length = Run(markdown, open, end);
            var search = open + length;
            var closed = false;
            while (search < end)
            {
                var next = markdown.IndexOf('`', search, end - search);
                if (next < 0)
                {
                    break;
                }
                var run = Run(markdown, next, end);
                if (run == length)
                {
                    spans.Add((open, next + run));
                    position = next + run;
                    closed = true;
                    break;
                }
                search = next + run;
            }
            if (!closed)
            {
                position = open + length;
            }
        }
        return spans;
    }

    private static int Run(string text, int start, int end)
    {
        var position = start;
        while (position < end && text[position] == '`')
        {
            position++;
        }
        return position - start;
    }

    /// <summary>The fence of a line that opens or closes fenced code, also inside quotes and list items.</summary>
    private static string? FenceMarker(ReadOnlySpan<char> line)
    {
        var match = Fence().Match(line.ToString());
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsBlank(ReadOnlySpan<char> text) => text.IsWhiteSpace();

    private static InvalidDataException Invalid() => new("A password reference in the document is not valid.");

    [GeneratedRegex("::?secret\\{", RegexOptions.CultureInvariant)]
    private static partial Regex Opener();

    /// <summary>The plugin's attribute rule: <c>name=value</c> or <c>name="value with spaces"</c>, after optional blanks.</summary>
    [GeneratedRegex("\\G[ \\t]*([A-Za-z][A-Za-z0-9]*)=(?:\"([^\"\\\\\\n]*)\"|([^\\s\"{}=\\\\]+))", RegexOptions.CultureInvariant)]
    private static partial Regex Attribute();

    [GeneratedRegex("^(?:[ \\t]*(?:>|[*+-]|\\d{1,9}[.)])(?=[ \\t]|$))*[ \\t]*(`{3,}|~{3,})", RegexOptions.CultureInvariant)]
    private static partial Regex Fence();

    [GeneratedRegex("^\\d{1,5}$", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();
}

/// <summary>One directive: where it is, whether it is the block form, and its secret id (null when it breaks the contract).</summary>
public readonly record struct SecretDirective(int Start, int Length, bool Block, Guid? SecretId);
