using System.Text.Json.Nodes;
using Atlas.Server.Features.Search.Domain;

namespace Atlas.Server.Features.Entities;

/// <summary>One identity key of a contact; a strong one (an address, an account) is the same person, a weak one only may be.</summary>
public sealed record IdentityKey(string Value, bool Strong);

/// <summary>
/// The one identity of a contact (contacts spec 2026-10-30 §1): the catalog merges by it (<see cref="Of"/>), the directory refuses and
/// links by it (ContactDirectory), the save and the migration pair snapshots by it. Keys in order of strength: each e-mail in lower
/// case, the internal account and the directory id (strong), each phone in E.164 and the name with the organization (weak).
/// </summary>
public static class EntityIdentity
{
    /// <summary>The catalog's key: a strong key, else the directory object it links to, else a weak key, else its name.</summary>
    public static string Of(JsonObject contact)
    {
        var keys = Keys(contact);
        if (keys.FirstOrDefault(key => key.Strong) is { } strong)
        {
            return strong.Value;
        }
        if (Text(contact["objectId"]) is { Length: > 0 } objectId && Guid.TryParse(objectId, out var id))
        {
            return "object:" + id;
        }
        return keys.FirstOrDefault()?.Value ?? "name:" + Normalize(Text(contact["name"])) + "|";
    }

    public static IReadOnlyList<IdentityKey> Keys(JsonObject contact)
    {
        var keys = new List<IdentityKey>();
        foreach (var email in Texts(contact["emails"]))
        {
            keys.Add(new IdentityKey("email:" + email.ToLowerInvariant(), true));
        }
        if (contact["internalUser"] is JsonObject user && Text(user["id"]) is { Length: > 0 } userId)
        {
            keys.Add(new IdentityKey("user:" + userId.ToLowerInvariant(), true));
        }
        if (Text(contact["directoryId"]) is { Length: > 0 } directoryId)
        {
            keys.Add(new IdentityKey("directory:" + directoryId, true));
        }
        foreach (var phone in Texts(contact["phones"]))
        {
            if (Phone(phone) is { } e164)
            {
                keys.Add(new IdentityKey("phone:" + e164, false));
            }
        }
        if (Normalize(Text(contact["organization"])) is { Length: > 0 } organization && Normalize(Text(contact["name"])) is { Length: > 0 } name)
        {
            keys.Add(new IdentityKey("name:" + name + "|" + organization, false));
        }
        return keys.DistinctBy(key => key.Value).ToArray();
    }

    /// <summary>
    /// A phone in E.164: separators dropped, 00 read as +, nine digits are a Czech number. Anything else is null, kept as written in the
    /// contact and left out of its identity.
    /// </summary>
    // shortcut: +420 is the only default and lengths are E.164's bounds, not per country; add libphonenumber once contacts live outside CZ/SK.
    public static string? Phone(string? phone)
    {
        var compact = new string((phone ?? "").Where(c => !(char.IsWhiteSpace(c) || c is '(' or ')' or '.' or '-' or '/')).ToArray());
        if (compact.StartsWith("00", StringComparison.Ordinal))
        {
            compact = "+" + compact[2..];
        }
        var plus = compact.StartsWith('+');
        var digits = plus ? compact[1..] : compact;
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return null;
        }
        return plus && digits.Length is >= 8 and <= 15 && digits[0] != '0' ? "+" + digits
            : !plus && digits.Length == 9 && digits[0] != '0' ? "+420" + digits
            : null;
    }

    /// <summary>Lower case without diacritics and with single spaces: „Jan  Kovařík“ and „jan kovarik“ are one person.</summary>
    public static string Normalize(string value) => string.Join(' ', SearchText.LookupWords(value));

    private static IEnumerable<string> Texts(JsonNode? node) => node is JsonArray items ? items.Select(Text).Where(text => text.Length > 0) : [];

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";
}
