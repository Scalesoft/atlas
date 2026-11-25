using System.Text.RegularExpressions;
using Xunit;

namespace Atlas.Server.Tests.Platform;

[Trait("Category", "Guard")]
public sealed partial class RowLockTextConventionTests
{
    /// <summary>
    /// PageLockingLoadConventionTests sees the locks asked of NHibernate. A row lock can also be written as text in native SQL,
    /// and then neither that test nor a decorator of IAtlasLocks sees it. Row locks belong in AtlasLocks (docs/architecture.md,
    /// Konvence kódu). This test reads every string literal of the server assembly for a FOR UPDATE, FOR NO KEY UPDATE,
    /// FOR SHARE or FOR KEY SHARE clause and allows one only in AtlasLocks and in the methods listed below, each with its reason
    /// and the number of such literals in it. The list is exact both ways: a new literal fails, and so does a listed one that
    /// is gone.
    /// What it cannot see: SQL that is not a literal of this assembly (the triggers and functions of the migrations are
    /// embedded files) and a clause put together from pieces.
    /// </summary>
    [Fact]
    public void A_row_lock_is_written_as_text_only_where_the_list_says()
    {
        var found = ServerIl.Literals
            .Where(literal => literal.Type != typeof(AtlasLocks) && RowLock().IsMatch(literal.Text))
            .GroupBy(literal => literal.Site, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var problems = new List<string>();
        foreach (var (site, literals) in found.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!Allowed.TryGetValue(site, out var allowed))
            {
                problems.Add($"{site}: {literals} literal(s) take a row lock written as SQL text. Row locks go through IAtlasLocks, or the method is listed here with its reason.");
            }
            else if (allowed != literals)
            {
                problems.Add($"{site}: listed with {allowed} literal(s) that take a row lock, but has {literals}.");
            }
        }
        problems.AddRange(Allowed.Keys.Where(site => !found.ContainsKey(site)).Select(site => $"{site}: listed, but takes no row lock as text any more."));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        // The scan sees the place where row locks belong, so finding none elsewhere is not a scan that sees nothing.
        Assert.Contains(ServerIl.Literals, literal => literal.Type == typeof(AtlasLocks) && RowLock().IsMatch(literal.Text));
    }

    /// <summary>Where a row lock is written as text today, with the number of such literals in the method.</summary>
    private static readonly Dictionary<string, int> Allowed = new(StringComparer.Ordinal)
    {
        // The assistant's own rows (AiConversation, AiMessage): one turn at a time per conversation, and
        // retention batches that skip rows a running turn holds (SKIP LOCKED), which IAtlasLocks does not offer. No content row.
        ["Atlas.Server.Features.Assistant.Persistence.ConversationStore.LockOwnedAsync"] = 1,
        ["Atlas.Server.Features.Assistant.Persistence.ConversationStore.LockAllOwnedAsync"] = 1,
        ["Atlas.Server.Features.Assistant.Persistence.ConversationStore.LockExpiredAsync"] = 1,
        ["Atlas.Server.Features.Assistant.Persistence.ConversationStore.DeleteAsync"] = 1,
        ["Atlas.Server.Features.Assistant.Persistence.ConversationStore.CancelStaleAsync"] = 1,
        ["Atlas.Server.Features.Assistant.Persistence.ConversationStore.MessageAsync"] = 1,
        // The AI index's own rows (IndexGeneration, IndexTask): the worker claims tasks with SKIP LOCKED and locks one task.
        ["Atlas.Server.Features.AiIndex.IndexStore.GenerationAsync"] = 1,
        ["Atlas.Server.Features.AiIndex.IndexStore.ClaimAsync"] = 1,
        ["Atlas.Server.Features.AiIndex.IndexStore.LockTaskAsync"] = 1,
        // A Page row FOR SHARE, by the index worker: it runs without a request account, outside the page gate, and reads the
        // page's publication and eligibility in the statement that locks it. The one content row locked as text outside AtlasLocks.
        ["Atlas.Server.Features.AiIndex.IndexStore.LockPageAsync"] = 1,
        // A Board row FOR SHARE for a board read (items and sequence of one committed batch): the optional Boards module's own row,
        // which IAtlasLocks (the platform) does not know; NHibernate has no FOR SHARE lock mode. Batches take it FOR UPDATE through
        // GetForUpdateAsync.
        ["Atlas.Server.Features.Boards.Domain.BoardAccess.ReadSharedAsync"] = 1,
        // A ContentResource envelope FOR KEY SHARE before the entity catalog replaces its occurrences: the order a permanent delete
        // takes (envelope FOR UPDATE, then the occurrences), so the two never deadlock (PER3-2). AtlasLocks locks no envelope.
        ["Atlas.Server.Features.Entities.EntityCatalog.RefreshAsync"] = 1,
        // A Page row FOR KEY SHARE (SKIP LOCKED for the outbox and the job) before the requirement index replaces the page's rows and
        // marks: the order a writer of the page takes (Page FOR UPDATE, then the marks: the 3.6 conversion), so the two never deadlock
        // (4.0 first start). A static refresh on the caller's session, without IAtlasLocks; IAtlasLocks offers no SKIP LOCKED.
        ["Atlas.Server.Features.Projector.Persistence.ProjectorIndex.RefreshAsync"] = 1,
        // PageVerification rows FOR UPDATE SKIP LOCKED in the daily notice batch: the feature's own rows, claimed so two runs never
        // send the same notice; IAtlasLocks offers no SKIP LOCKED. No content row.
        ["Atlas.Server.Features.PageVerifications.SendVerificationNoticesHandler.Handle"] = 1,
        // A Space row FOR UPDATE before a page's insert, in the NHibernate listener that writes what TR_Page_PrepareOrganization did
        // (2026110930): the listener runs inside the flush on the session's own connection, below the services that IAtlasLocks is
        // one of; it serialises the creates of a space as the trigger's lock did.
        ["Atlas.Server.Platform.Persistence.ContentRowEffects.Prepare"] = 1,
        // A Job row FOR SHARE before a request merges its run: a resume committing meanwhile waits for it or is read by it, so no
        // queued run is stranded. Job is the jobs runtime's own row, which IAtlasLocks does not know; no content row.
        ["Atlas.Server.Platform.Jobs.JobRequests.QueueAsync"] = 1,
    };

    [GeneratedRegex(@"\bfor\s+(no\s+key\s+update|key\s+share|update|share)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RowLock();
}
