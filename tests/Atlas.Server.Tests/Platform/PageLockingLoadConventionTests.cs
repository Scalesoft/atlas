using System.Reflection;
using Xunit;
using ISession = NHibernate.ISession;
using LockMode = NHibernate.LockMode;

namespace Atlas.Server.Tests.Platform;

[Trait("Category", "Guard")]
public sealed class PageLockingLoadConventionTests
{
    /// <summary>
    /// ISession.GetForUpdateAsync returns an instance the session holds at LockMode.Upgrade or above without a statement, because
    /// it takes such an instance as read under its lock. That holds only while nothing else raises the lock mode of a Page
    /// instance without re-reading it: a locking Get or Load of a held instance, ISession.Lock, and a query with a lock mode all
    /// take the row lock and leave the instance as it was loaded before the lock (the stale read fixed in 50d9693f and 3d596677).
    /// So every locking load of a Page goes through GetForUpdateAsync, and this test reads the IL of the server assembly for
    /// every other call into NHibernate that names a lock mode. A call on Page fails it, and so does a call whose entity the
    /// signature does not name (Lock, Refresh, a query's lock mode): pin such a call below only after checking that the entity
    /// is not a Page, or that the instance cannot be stale there (it is re-read under the lock right after, or nothing else can
    /// write the row in between). The list is exact: a pinned call that is gone fails too, so the list cannot outlive its reasons.
    /// What this cannot see: native SQL row locks (IAtlasLocks) do not touch the lock mode NHibernate keeps, so
    /// GetForUpdateAsync re-reads after them; and a Page changed and flushed before its locking load is at LockMode.Write with
    /// the other columns as loaded, which no static check finds. The rule for that is in SessionExtensions: a page is never
    /// changed before its gate.
    /// The Smoke tag is deliberate: a locking load would be added in a feature, and for such a change tests/changed.ps1 runs
    /// only that feature's tests, not the Smoke tests. The tag makes this guard run in tests/smoke.ps1, before a commit or
    /// handoff, and not only in the full suite.
    /// </summary>
    [Trait("Category", "Smoke")]
    [Fact]
    public void Every_locking_load_of_a_Page_goes_through_GetForUpdateAsync()
    {
        var helper = typeof(SessionExtensions).FullName + "." + nameof(SessionExtensions.GetForUpdateAsync);
        var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var call in ServerIl.Calls)
        {
            if (call.Site != helper && Describe(call.Callee) is { } described)
            {
                var key = call.Site + ": " + described;
                found[key] = found.GetValueOrDefault(key) + 1;
            }
        }

        var problems = new List<string>();
        foreach (var (key, calls) in found)
        {
            if (!Pinned.TryGetValue(key, out var pinned))
            {
                problems.Add($"{key}: takes an NHibernate lock on a Page, or on an entity this test cannot name, outside ISession.GetForUpdateAsync.");
            }
            else if (pinned != calls)
            {
                problems.Add($"{key}: pinned with {pinned} call(s), but has {calls}.");
            }
        }
        problems.AddRange(Pinned.Keys.Where(key => !found.ContainsKey(key)).Select(key => $"{key}: pinned, but the call is gone (moved, renamed or converted)."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>The calls allowed beside the helper, with the number of calls in the method. Pinned, not endorsed.</summary>
    private static readonly Dictionary<string, int> Pinned = new(StringComparer.Ordinal)
    {
        // A Page, re-read explicitly right after the locking load (two statements). It moves to the helper with the publish gate,
        // which changes its pinned lock trace and statement count.
        ["Atlas.Server.Features.Pages.Commands.PublishPageHandler.Handle: ISession.GetAsync<Page>"] = 1,
        // A Page, re-read under the lock right after the locking load. Content import is outside the page gate.
        ["Atlas.Server.Features.ContentImport.Commands.CommitImportHandler.Handle: ISession.GetAsync<Page>"] = 1,
        ["Atlas.Server.Features.ContentImport.Commands.CommitImportHandler.Handle: ISession.RefreshAsync (entity not named)"] = 1,
        // Not a Page: the vault, re-read under its lock.
        ["Atlas.Server.Features.Vaults.VaultAccess.RequireVaultAsync: ISession.RefreshAsync (entity not named)"] = 1,
    };

    /// <summary>
    /// A call into NHibernate that names a lock mode, when it is about a Page or does not say what it is about; null otherwise.
    /// A generic Get or Load names its entity, so one of another entity is none of this test's business.
    /// </summary>
    private static string? Describe(MethodBase callee)
    {
        var declaring = callee.DeclaringType;
        if (declaring is null || declaring.Assembly != typeof(ISession).Assembly || declaring == typeof(LockMode))
        {
            return null;
        }
        var namesLockMode = callee.GetParameters().Any(parameter => parameter.ParameterType == typeof(LockMode))
            || declaring.Name.Contains("LockBuilder", StringComparison.Ordinal);
        if (!namesLockMode)
        {
            return null;
        }
        if (!callee.IsGenericMethod)
        {
            return $"{declaring.Name}.{callee.Name} (entity not named)";
        }
        var entity = callee.GetGenericArguments()[0];
        return entity == typeof(Page) || entity.IsGenericParameter ? $"{declaring.Name}.{callee.Name}<{entity.Name}>" : null;
    }
}
