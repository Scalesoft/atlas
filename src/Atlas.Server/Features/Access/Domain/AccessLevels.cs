namespace Atlas.Server.Features.Access.Domain;

/// <summary>The one staircase of every kind as API strings with their rank: discover 10, read 20, write 30, manage 40.</summary>
public static class AccessLevels
{
    public const string Discover = "discover";
    public const string Read = "read";
    public const string Write = "write";
    public const string Manage = "manage";

    public static int Rank(string? level) => level switch
    {
        Discover => 10,
        Read => 20,
        Write => 30,
        Manage => 40,
        _ => 0
    };

    public static string? Highest(IEnumerable<string?> levels)
    {
        return levels.Where(level => Rank(level) > 0).MaxBy(Rank);
    }

    /// <summary>A stored space or page role (1 Reader, 2 Writer, 3 Admin or DocumentManager).</summary>
    public static string? FromRole(int role) => role switch
    {
        1 => Read,
        2 => Write,
        3 => Manage,
        _ => null
    };

    /// <summary>The role name SetSpaceGrant and SetPageGrant take; null for a level these kinds do not use.</summary>
    public static string? RoleName(string level) => level switch
    {
        Read => "Reader",
        Write => "Writer",
        Manage => "Admin",
        _ => null
    };
}
