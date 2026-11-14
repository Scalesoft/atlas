using System.Reflection;
using System.Text.RegularExpressions;
using Atlas.Server.Features.Administration.Domain;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Features.Spaces.Domain;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.AccessAudit;

/// <summary>
/// Since 3.6 (no logic in the database, migration 2026110941) no trigger writes the history and audit of accounts, page access modes and
/// access intervals: the code that changes them does. The compiler cannot see a writer that forgets it, so this test reads the server's IL
/// per method (closures and state machines count as the method they were written in): a method that changes UserAccount.IsActive,
/// IsWikiAdmin or SecurityStamp, creates an account, creates a page or changes Page.OwnAccess, opens or closes a GroupMember, SpaceGrant
/// or ResourceGrant, or holds an INSERT or UPDATE of those tables in its SQL text, calls AccountHistory, PageAccessModeHistory or
/// AccessAuditLog.OpenedAsync/ClosedAsync, or is listed below with the method that calls them for it. The list is exact both ways.
/// </summary>
[Trait("Category", "Guard")]
public sealed partial class AccessAuditCoverageTests
{
    /// <summary>Writers that leave the history or audit to another method, by site, and which one writes it.</summary>
    private static readonly Dictionary<string, string> Delegated = new(StringComparer.Ordinal)
    {
        ["Atlas.Server.Features.Access.Domain.RevokeAllRows.CloseLocalMembershipsAsync"] = "returns the closed ids; RevokeAllAccessHandler.Handle audits them",
        ["Atlas.Server.Features.Administration.AdministrationEndpoints.MapAdministration"] = "a transient account for the sign-in cookie, never saved",
        ["Atlas.Server.Features.Auth.Builtin.Commands.BuiltinLoginHandler.Handle"] = "a transient account for the password hasher, never saved",
        ["Atlas.Server.Features.Auth.OpenIdConnect.Commands.ResolveExternalIdentityHandler.NewExternalAccount"] =
            "builds the account; ResolveExternalIdentityHandler.Handle saves it and records its history",
        ["Atlas.Server.Features.Hub.Commands.ApplyHubSyncHandler.AddMembershipAsync"] = "returns the opened ids; ApplyObservationAsync audits them",
        ["Atlas.Server.Features.PageOrganization.Commands.MovePageToSpaceHandler.Handle"] =
            "a transient destination page for the parent check, never saved; PageItemAccess.ResetAsync records the moved pages' access modes",
    };

    [Fact]
    public void Every_writer_of_accounts_access_modes_and_access_intervals_records_their_history_and_audit()
    {
        var problems = CoverageScan.Problems(WritesCall, WritesText, Records, Delegated, "records no history or audit (AccountHistory, PageAccessModeHistory, "
            + "AccessAuditLog.OpenedAsync/ClosedAsync)", minimum: 15);
        Assert.True(problems.Length == 0, string.Join("\n", problems));
    }

    private static bool WritesCall(MethodBase callee)
        => CoverageScan.Sets(callee, typeof(UserAccount), nameof(UserAccount.IsActive), nameof(UserAccount.IsWikiAdmin), nameof(UserAccount.SecurityStamp))
            || CoverageScan.Sets(callee, typeof(Page), nameof(Page.OwnAccess))
            || CoverageScan.Sets(callee, typeof(GroupMember), nameof(GroupMember.ActiveTo))
            || CoverageScan.Sets(callee, typeof(SpaceGrant), nameof(SpaceGrant.ActiveTo))
            || CoverageScan.Sets(callee, typeof(ResourceGrant), nameof(ResourceGrant.ActiveTo))
            || callee.IsConstructor && callee.DeclaringType is { } type
                && (type == typeof(UserAccount) || type == typeof(Page) || type == typeof(GroupMember) || type == typeof(SpaceGrant) || type == typeof(ResourceGrant));

    private static bool WritesText(string text) => SqlWrite().IsMatch(text);

    private static bool Records(MethodBase callee)
        => callee.DeclaringType == typeof(AccountHistory) || callee.DeclaringType == typeof(PageAccessModeHistory)
            || callee.DeclaringType == typeof(AccessAuditLog) && callee.Name is nameof(AccessAuditLog.OpenedAsync) or nameof(AccessAuditLog.ClosedAsync);

    [GeneratedRegex("""
        (UPDATE|INSERT\s+INTO)\s+(ONLY\s+)?(\w+\.)?"(UserAccount|GroupMember|SpaceGrant|ResourceGrant)"
        | INSERT\s+INTO\s+(\w+\.)?"Page"\s*\(
        | UPDATE\s+(ONLY\s+)?(\w+\.)?"Page"\s[^;]*\bSET\b[^;]*"OwnAccess"\s*=
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex SqlWrite();
}

/// <summary>The scan of AccessAuditCoverageTests and ShareRevocationCoverageTests over ServerIl.</summary>
internal static class CoverageScan
{
    public static bool Sets(MethodBase callee, Type type, params string[] properties)
        => callee.DeclaringType == type && callee.Name.StartsWith("set_", StringComparison.Ordinal) && properties.Contains(callee.Name[4..]);

    public static string[] Problems(Func<MethodBase, bool> writesCall, Func<string, bool> writesText, Func<MethodBase, bool> records,
        IReadOnlyDictionary<string, string> delegated, string missing, int minimum)
    {
        var writers = ServerIl.Calls.Where(call => writesCall(call.Callee)).Select(call => call.Site)
            .Concat(ServerIl.Literals.Where(literal => writesText(literal.Text)).Select(literal => literal.Site))
            .ToHashSet(StringComparer.Ordinal);
        var recording = ServerIl.Calls.Where(call => records(call.Callee)).Select(call => call.Site).ToHashSet(StringComparer.Ordinal);
        Assert.True(writers.Count >= minimum, "The scan sees too few writers: " + string.Join(", ", writers.Order()));
        return writers.Where(site => !recording.Contains(site) && !delegated.ContainsKey(site)).Order(StringComparer.Ordinal)
            .Select(site => $"{site}: {missing}, and is not listed with the method that does.")
            .Concat(delegated.Keys.Where(site => !writers.Contains(site)).Select(site => $"{site}: listed, but writes nothing any more."))
            .Concat(delegated.Keys.Where(site => writers.Contains(site) && recording.Contains(site)).Select(site => $"{site}: listed, but records itself now."))
            .ToArray();
    }
}
