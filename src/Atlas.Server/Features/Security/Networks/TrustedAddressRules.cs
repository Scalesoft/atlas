using Atlas.Server.Features.Security.Domain;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>Validation and warnings of trusted networks and proxies (spec 5.2): errors belong to a field and block the save;
/// warnings (normalization, duplicates, overlaps, the whole internet) are shown before and after saving.</summary>
public static class TrustedAddressRules
{
    public const int NameMaxLength = 200;
    public static readonly IReadOnlySet<string> NetworkKinds = new HashSet<string>(StringComparer.Ordinal) { "lan", "vpn", "other" };

    public static string Name(string? name)
    {
        return Rules.Required(name, "Název", NameMaxLength);
    }

    /// <summary>lan, vpn or other for a network; nothing for a proxy.</summary>
    public static string? Kind(string purpose, string? kind)
    {
        if (purpose == TrustedAddress.ProxyPurpose)
        {
            return string.IsNullOrEmpty(kind) ? null : throw new ApiException(400, "Proxy nemá druh sítě.");
        }
        return kind is not null && NetworkKinds.Contains(kind) ? kind : throw new ApiException(400, "Vyberte druh sítě: LAN, VPN nebo jiná.");
    }

    /// <summary>The normalized range; a proxy may not be every address (that would trust any forwarded header).</summary>
    public static AddressRangeParse Range(string purpose, string? range)
    {
        var parsed = AddressRange.Parse(range);
        if (parsed.Error is { } error)
        {
            throw new ApiException(400, error);
        }
        if (purpose == TrustedAddress.ProxyPurpose && parsed.Range!.IsEverything)
        {
            throw new ApiException(400, "Důvěryhodná proxy nemůže být celý internet; zadejte adresy skutečných proxy.");
        }
        return parsed;
    }

    public static IReadOnlyList<string> Warnings(string purpose, AddressRangeParse parsed, IEnumerable<TrustedRule> others)
    {
        var range = parsed.Range!;
        var warnings = new List<string>();
        if (parsed.Normalized)
        {
            warnings.Add($"Rozsah byl upraven na {range.Text}: bity adresy za délkou prefixu se nulují.");
        }
        if (range.IsEverything)
        {
            warnings.Add("Rozsah zahrnuje celý internet: výjimka z nového přihlášení by platila odkudkoli.");
        }
        foreach (var other in others.Where(o => o.Purpose == purpose))
        {
            if (other.Range.Text == range.Text)
            {
                warnings.Add($"Stejný rozsah už má pravidlo „{other.Name}“.");
            }
            else if (other.Range.Overlaps(range))
            {
                warnings.Add($"Překrývá se s pravidlem „{other.Name}“ ({other.Range.Text}).");
            }
        }
        return warnings;
    }
}
