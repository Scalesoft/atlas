using System.Text.RegularExpressions;

namespace Atlas.Server.Features.AiWriting;

/// <summary>Server authority for E7 working text: the browser detector improves UX but cannot authorize sending a secret.</summary>
public static partial class WritingSecrets
{
    private const string Keywords = "hesl[oa]|password|passwd|pwd|token|secret|api[ _-]?key|api klíč|access[_-]?key|private[_-]?key|client[_-]?secret";
    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY(?: BLOCK)?-----")]
    private static partial Regex PrivateKey();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_-])(?:sk-[A-Za-z0-9-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{22,}|glpat-[A-Za-z0-9_-]{20,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})(?![\p{L}\p{N}_])")]
    private static partial Regex ProviderKey();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:[A-Za-z]+[_.-])*(?:" + Keywords + @")(?:[_.-][A-Za-z]+)*\s*[:=]\s*(?<value>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();
    [GeneratedRegex(@"(?<![\p{L}\p{N}_])(?:heslo|password|pwd|token|api klíč)(?:\s+(?:je|is))?[ \t\u00A0]+(?<value>\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex Bare();
    [GeneratedRegex(@"(?i)\b(?:sha-?\d*|md5|hash\p{L}*|checksum\p{L}*|commit\p{L}*|git|revi[sz]\p{L}*|otisk\p{L}*|kontrolní\s+součet\p{L}*)\b")]
    private static partial Regex HashContext();
    [GeneratedRegex(@"\S+")]
    private static partial Regex Words();
    [GeneratedRegex(@"[A-Z]+(?![a-z])|[A-Z]?[a-z]+|\d+")]
    private static partial Regex Pieces();
    [GeneratedRegex(@"^[A-Za-z0-9+/=_.~-]+$")]
    private static partial Regex MachineToken();
    [GeneratedRegex(@"^[0-9a-f]+$", RegexOptions.IgnoreCase)]
    private static partial Regex Hex();
    [GeneratedRegex(@"^\d+\.\d+\.\d+[+-]")]
    private static partial Regex Version();
    [GeneratedRegex(@"^(?:[0-9a-f]{0,8}[:.-])+[0-9a-f]{1,12}$", RegexOptions.IgnoreCase)]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^[A-Za-z]*\d*$")]
    private static partial Regex IdentifierPart();

    public static bool Contains(string text)
    {
        if (PrivateKey().IsMatch(text) || ProviderKey().IsMatch(text)) return true;
        if (Assignment().Matches(text).Any(x => Literal(Trim(x.Groups["value"].Value), false))
            || Bare().Matches(text).Any(x => Literal(Trim(x.Groups["value"].Value), true))) return true;
        var hash = HashContext().IsMatch(text);
        return Words().Matches(text).Any(x => HighEntropy(AssignedValue(Trim(x.Value)), hash));
    }

    private static string Trim(string value) => value.TrimStart('"', '\'', '„', '“', '‚', '‘', '«', '`')
        .TrimEnd('"', '\'', '“', '”', '‘', '’', '»', '`', '.', ',', ';', ':', ')', ']');

    private static string AssignedValue(string value)
    {
        var separator = value.IndexOfAny(['=', ':']);
        return separator > 0 && value[..separator].All(x => char.IsAsciiLetterOrDigit(x) || x is '_' or '.' or '-')
            && value[(separator + 1)..].Any(x => x != '=') ? value[(separator + 1)..] : value;
    }

    private static bool Literal(string value, bool bare)
    {
        if (value.Length < 8 || value.Any(x => x < '!' || x > '~') || LookAlike(value)
            || value.IndexOfAny(['(', ')', '{', '}', '<', '>', '[', ']', '"', '\'', '`', ';']) >= 0) return false;
        var path = value.Replace("?.", ".").Replace("!.", ".");
        if (path.IndexOfAny(['.', '_', '/', ':']) >= 0 && path.Split(['.', '_', '/', ':', '-']).All(x => IdentifierPart().IsMatch(x))) return false;
        if (!value.Any(char.IsAsciiDigit) && value.All(x => char.IsAsciiLetter(x) || x is '-' or '_')) return false;
        return !bare || Classes(value) >= 2 && value.Any(x => !char.IsAsciiLetter(x));
    }

    private static bool HighEntropy(string value, bool hash)
    {
        if (value.Length < 12 || !MachineToken().IsMatch(value) || Classes(value) < 2 || LookAlike(value) || Version().IsMatch(value)) return false;
        if (Hex().IsMatch(value))
        {
            if (hash || value.Length < 32) return false;
        }
        else if (!Random(value)) return false;
        return value.GroupBy(x => x).Sum(x => { var p = (double)x.Count() / value.Length; return -p * Math.Log2(p); }) >= 3.5;
    }

    private static bool Random(string value)
    {
        var pieces = Pieces().Matches(value);
        if (pieces.Count == 0 || (double)pieces.Sum(x => x.Length) / pieces.Count > 2.5) return false;
        var pairs = 0;
        var changes = 0;
        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiLetterOrDigit(value[index - 1]) || !char.IsAsciiLetterOrDigit(value[index])) continue;
            pairs++;
            if (Kind(value[index - 1]) != Kind(value[index])) changes++;
        }
        return pairs > 0 && (double)changes / pairs >= .45;
    }

    private static int Kind(char value) => char.IsAsciiDigit(value) ? 0 : char.IsAsciiLetterLower(value) ? 1 : 2;
    private static int Classes(string value) => (value.Any(char.IsAsciiLetterLower) ? 1 : 0) + (value.Any(char.IsAsciiLetterUpper) ? 1 : 0)
        + (value.Any(char.IsAsciiDigit) ? 1 : 0) + (value.Any(x => !char.IsAsciiLetterOrDigit(x)) ? 1 : 0);
    private static bool LookAlike(string value) => value.Contains("://", StringComparison.Ordinal) || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
        || value.Contains('@') || value.Contains('\\') || value.StartsWith('/') || value.StartsWith("./", StringComparison.Ordinal)
        || value.StartsWith("../", StringComparison.Ordinal) || Identifier().IsMatch(value)
        || value.Contains("${", StringComparison.Ordinal) || value.Contains("{{", StringComparison.Ordinal)
        || value.StartsWith('$') || value.StartsWith('<') || value.StartsWith('%') || value.Distinct().Take(2).Count() == 1;
}
