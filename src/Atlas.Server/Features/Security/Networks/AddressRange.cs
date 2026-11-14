using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Atlas.Server.Features.Security.Networks;

/// <summary>
/// A normalized IPv4 or IPv6 network: a single address is /32 or /128, host bits beyond the prefix are cleared and an IPv4-mapped
/// IPv6 range becomes its IPv4 range. <see cref="Text"/> is the stored and displayed form.
/// </summary>
public sealed partial record AddressRange(IPAddress Network, int PrefixLength)
{
    public const int MaxTextLength = 50;

    public bool IsIPv6 => Network.AddressFamily == AddressFamily.InterNetworkV6;

    public int MaxPrefix => IsIPv6 ? 128 : 32;

    public bool IsSingleAddress => PrefixLength == MaxPrefix;

    /// <summary>0.0.0.0/0 or ::/0: every address of its family.</summary>
    public bool IsEverything => PrefixLength == 0;

    public string Text => Network + "/" + PrefixLength.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => Text;

    public bool Contains(IPAddress? address)
    {
        var normalized = Normalize(address);
        return normalized is not null && normalized.AddressFamily == Network.AddressFamily
            && new IPNetwork(Network, PrefixLength).Contains(normalized);
    }

    /// <summary>Same family and one range contains the other.</summary>
    public bool Overlaps(AddressRange other)
    {
        return other.Network.AddressFamily == Network.AddressFamily && (Contains(other.Network) || other.Contains(Network));
    }

    /// <summary>An IPv4-mapped IPv6 address as IPv4, without a scope; null stays null.</summary>
    public static IPAddress? Normalize(IPAddress? address)
    {
        if (address is null)
        {
            return null;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }
        // ScopeId throws for IPv4.
        return address.AddressFamily != AddressFamily.InterNetworkV6 || address.ScopeId == 0 ? address : new IPAddress(address.GetAddressBytes());
    }

    /// <summary>
    /// Parses an address or CIDR entered by an administrator. Strict: dotted IPv4 needs four decimal parts (no "10.1" shorthand),
    /// zone ids and hostnames are refused. Normalized is true when host bits had to be cleared.
    /// </summary>
    public static AddressRangeParse Parse(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
        {
            return AddressRangeParse.Invalid("Zadejte IP adresu nebo rozsah CIDR, například 192.168.10.0/24.");
        }
        if (value.Length > MaxTextLength)
        {
            return AddressRangeParse.Invalid("Adresa je příliš dlouhá.");
        }
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        var addressText = slash < 0 ? value : value[..slash];
        var prefixText = slash < 0 ? null : value[(slash + 1)..];
        if (!TryParseRaw(addressText, out var address))
        {
            return AddressRangeParse.Invalid("Toto není platná adresa IPv4 ani IPv6.");
        }
        var max = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        var prefix = max;
        if (prefixText is not null && (!PrefixDigits().IsMatch(prefixText)
            || !int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > max))
        {
            return AddressRangeParse.Invalid($"Délka prefixu musí být číslo od 0 do {max}.");
        }
        if (address.IsIPv4MappedToIPv6)
        {
            if (prefix < 96)
            {
                return AddressRangeParse.Invalid("Rozsah mapovaných adres IPv4 zadejte jako IPv4.");
            }
            address = address.MapToIPv4();
            prefix -= 96;
        }
        var network = Mask(address, prefix);
        return new AddressRangeParse(new AddressRange(network, prefix), !network.Equals(address), null);
    }

    /// <summary>One address, strictly (see <see cref="Parse"/>); IPv4-mapped IPv6 becomes IPv4.</summary>
    public static bool TryParseAddress(string? text, out IPAddress address)
    {
        if (!TryParseRaw(text, out address))
        {
            return false;
        }
        address = Normalize(address)!;
        return true;
    }

    private static bool TryParseRaw(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Length > MaxTextLength || value.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }
        if (!value.Contains(':', StringComparison.Ordinal) && !DottedQuad().IsMatch(value))
        {
            return false;
        }
        if (!IPAddress.TryParse(value, out var parsed))
        {
            return false;
        }
        address = parsed;
        return true;
    }

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - i * 8, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }
        return new IPAddress(bytes);
    }

    // No leading zeros: the platform parser would read "010" as octal.
    [GeneratedRegex(@"\A(0|[1-9]\d{0,2})(\.(0|[1-9]\d{0,2})){3}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex DottedQuad();

    [GeneratedRegex(@"\A\d{1,3}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex PrefixDigits();
}
