namespace Atlas.Server.Features.PersonalApi;

/// <summary>What a personal token may do (owner's decision for 3.6): read, or read and write. 3.6 has no drafts: a write is a live edit,
/// as a person makes it, and what readers see follows the page's publication setting (Ručně/Automaticky/Živě). The former scopes draft,
/// publish and direct are all write (migration 2026110684). Canonical order: read, write.</summary>
public static class ApiScopes
{
    public const string Read = "read";
    public const string Write = "write";

    public static readonly IReadOnlyList<string> All = [Read, Write];

    /// <summary>The scopes in canonical order (a set has none), unknown values dropped.</summary>
    public static string[] Ordered(IEnumerable<string> scopes)
    {
        var set = scopes as ISet<string> ?? scopes.ToHashSet(StringComparer.Ordinal);
        return All.Where(set.Contains).ToArray();
    }

    /// <summary>A valid token scope set: distinct known scopes, read included. A former scope (draft, publish, direct) is refused, never
    /// mapped: a client that asks for less than write must not get write silently.</summary>
    public static bool IsValid(IReadOnlyCollection<string>? scopes)
        => scopes is { Count: >= 1 } && scopes.All(All.Contains) && scopes.Distinct(StringComparer.Ordinal).Count() == scopes.Count
           && scopes.Contains(Read);

    /// <summary>The Czech name of a scope, as the profile shows it.</summary>
    public static string Label(string scope) => scope == Write ? "Zápis" : "Čtení";
}

/// <summary>The transports a personal token may be used on. The server decides the channel of a request from its path, never the client.</summary>
public static class ApiChannels
{
    /// <summary>The personal REST API under /api/v1.</summary>
    public const string Rest = "rest";
    /// <summary>The MCP endpoint (/mcp, added by the MCP module).</summary>
    public const string Mcp = "mcp";

    public static readonly IReadOnlyList<string> All = [Rest, Mcp];

    public static string[] Ordered(IEnumerable<string> channels)
    {
        var set = channels as ISet<string> ?? channels.ToHashSet(StringComparer.Ordinal);
        return All.Where(set.Contains).ToArray();
    }

    public static bool IsValid(IReadOnlyCollection<string>? channels)
        => channels is { Count: >= 1 } && channels.All(All.Contains) && channels.Distinct(StringComparer.Ordinal).Count() == channels.Count;

    /// <summary>The channel a bearer request on this path uses, or null where personal tokens are not accepted.</summary>
    public static string? ForPath(PathString path) =>
        path.StartsWithSegments("/api/v1") ? Rest
        : path.StartsWithSegments("/mcp") ? Mcp
        : null;
}

/// <summary>The ceiling a WikiAdmin sets per account (UserApiPermission.Level). The effective scopes of a token are its own scopes within the
/// owner's current level, computed at every authentication, so lowering the level applies to the next request of every token.</summary>
public static class ApiLevels
{
    public const string None = "none";
    public const string Read = "read";
    public const string Write = "write";

    public static readonly IReadOnlyList<string> All = [None, Read, Write];

    public static bool IsKnown(string? level) => level is not null && All.Contains(level);

    /// <summary>The scopes a token of this level may hold: none → [], read → [read], write → [read, write].</summary>
    public static IReadOnlyList<string> Scopes(string? level) => level switch
    {
        Read => [ApiScopes.Read],
        Write => ApiScopes.All,
        _ => []
    };

    /// <summary>Token scopes ∩ level scopes, in canonical order.</summary>
    public static string[] Effective(IEnumerable<string> tokenScopes, string? level)
    {
        var allowed = Scopes(level);
        return ApiScopes.Ordered(tokenScopes).Where(allowed.Contains).ToArray();
    }
}
