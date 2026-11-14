using Atlas.Server.Features.Access.Contracts;

namespace Atlas.Server.Features.Access.Domain;

/// <summary>The via values of a source, their labels and the one order of sources: effective first, then the strongest level.</summary>
public static class AccessSources
{
    public const string Direct = "direct";
    public const string Group = "group";
    public const string Automatic = "automatic";
    public const string Derived = "derived";

    public const string DirectLabel = "Přímo přidělený přístup";
    public const string WikiAdminLabel = "Správce wiki";
    public const string SpaceRoleLabel = "Z role v prostoru";

    public static string SpaceAdminLabel(string spaceName) => $"Správce prostoru {spaceName}";

    /// <summary>A grant row as a source: direct for the principal's own grant, group (named) when it reaches a person through one.</summary>
    public static AccessEntrySource Grant(Guid grantId, Guid? groupId, string? groupName, string level, DateTime activeFrom, DateTime? startAt,
        DateTime? expiresAt, bool effective, bool viaGroup)
    {
        return new AccessEntrySource(viaGroup ? Group : Direct, viaGroup ? groupName ?? "" : DirectLabel, level, groupId, grantId,
            AdministrationVersion.Format(activeFrom), startAt, expiresAt, effective);
    }

    public static IReadOnlyList<AccessEntrySource> Ordered(IEnumerable<AccessEntrySource> sources)
    {
        return sources.OrderByDescending(source => source.Effective)
            .ThenByDescending(source => AccessLevels.Rank(source.Level))
            .ThenBy(source => source.Via switch { Automatic => 0, Direct => 1, Group => 2, _ => 3 })
            .ThenBy(source => source.Label, StringComparer.CurrentCulture)
            .ToArray();
    }

    /// <summary>The highest level among the effective sources; null when none is effective.</summary>
    public static string? Level(IEnumerable<AccessEntrySource> sources)
    {
        return AccessLevels.Highest(sources.Where(source => source.Effective).Select(source => source.Level));
    }
}
