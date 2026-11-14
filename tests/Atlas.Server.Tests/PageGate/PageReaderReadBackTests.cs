using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using Xunit;
// The folder's namespace hides the type of the same name.
using Gate = Atlas.Server.Platform.Identity.PageGate;

namespace Atlas.Server.Tests.PageGate;

/// <summary>
/// PageReader as the read of a command's answer: the command holds the page it changed, and the reader locks it again, which
/// re-reads the row. Pinned on the reader itself, with a change that is still only in the session when the read begins.
/// The reader has three ways in: the deciding read (ReadAsync), which restore and create also use for their answer; the
/// read-back on the proof of an EditablePage (ReadEditedAsync); and the same read-back without that proof, for the handlers
/// that still open their page themselves (ReadEditedWithoutProofAsync).
/// </summary>
public sealed class PageReaderReadBackTests
{
    /// <summary>
    /// The reader flushes before it locks: the lock of a page the session holds re-reads the row, and a change that was not
    /// flushed yet would be dropped by that, silently, and the answer would carry the old row.
    /// </summary>
    [PostgreSqlTheory]
    [InlineData("ReadAsync")]
    [InlineData("ReadEditedAsync")]
    [InlineData("ReadEditedWithoutProofAsync")]
    public async Task A_page_changed_before_its_read_back_is_read_with_the_change(string way)
    {
        await using var f = await PageGateFixture.CreateAsync();
        var admin = await f.Host.SeedUserAsync("admin", isWikiAdmin: true);
        var page = await f.PublishedPageAsync("Read back");

        var (answer, sent) = await ReadBackAsync(f, admin, page.Id, way, edited => edited.Page.IsTemplate = true);

        Assert.True(answer.IsTemplate);
        // The update goes out first, then the page is locked again and its row read under the lock (the account stays locked from the gate).
        Assert.Equal(["update Page", "Page FOR SHARE", "read Page"], LockTrace.Of(sent));
        using var session = f.Host.OpenSession();
        Assert.True((await session.GetAsync<Page>(page.Id)).IsTemplate);
    }

    /// <summary>
    /// The EditablePage is the decision: the read-back on its proof does not decide the page a second time. It sends the
    /// statements of the read-back without proof, text for text: two to lock and re-read the page (the account stays locked and
    /// checked from the gate), then the reads of the answer. The deciding read sends one
    /// more in the same transaction, the grants of the page; the grants of the space and the wiki administrator status are
    /// remembered from the edit gate (read once per transaction since e28bb5c4f).
    /// </summary>
    [PostgreSqlFact]
    public async Task The_read_back_of_an_edited_page_does_not_decide_it_again()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Read back");

        var (proven, onProof) = await ReadBackAsync(f, f.Writer, page.Id, "ReadEditedAsync");
        var (_, withoutProof) = await ReadBackAsync(f, f.Writer, page.Id, "ReadEditedWithoutProofAsync");
        var (_, decided) = await ReadBackAsync(f, f.Writer, page.Id, "ReadAsync");

        Assert.Equal(withoutProof, onProof);
        Assert.Equal(["Page FOR SHARE", "read Page"], LockTrace.Of(onProof));
        // The two, the working revision and the decoration.
        Assert.Equal(4, onProof.Count);
        Assert.DoesNotContain(onProof, sql => sql.Contains("\"EffectiveWikiAdmin\"", StringComparison.Ordinal) || sql.Contains("Grant\"", StringComparison.Ordinal));
        Assert.Equal(onProof.Count + 1, decided.Count);
        // It reads as an editor, as the command decided.
        Assert.True(proven.CanEdit);
    }

    /// <summary>
    /// The proof is good for its transaction only. A capability kept beyond it fails the read-back as a programming error
    /// before anything is sent, the flush of a pending change included.
    /// </summary>
    [PostgreSqlFact]
    public async Task The_read_back_refuses_a_capability_of_an_ended_transaction_before_any_statement()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Read back");
        f.Host.Actor.Current = f.Writer;
        using var scope = f.Host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>();
        EditablePage edited;
        using (var taken = session.BeginTransaction())
        {
            edited = (await scope.ServiceProvider.GetRequiredService<Gate>().EditAsync(page.Id, AccessOperation.EditContent, default)).Require();
            await taken.CommitAsync();
        }

        using var later = session.BeginTransaction();
        edited.Page.IsTemplate = true;
        using var recording = SqlRecording.Start();
        await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<PageReader>().ReadEditedAsync(edited, default));
        Assert.Empty(recording.Statements);
    }

    /// <summary>
    /// The proof is good for the gate that handed it out only. The reader reads on the session of its own scope, and a
    /// capability of another scope stands for locks of another session. Its transaction may still run, so the gate that
    /// handed it out calls it held: a reader that asked that gate would take its own locks, wait for the page row the other
    /// scope holds FOR UPDATE and then build the page as an editor for its own account, which nothing decided. The reader
    /// asks its own gate, and the read-back fails as a programming error before anything is sent.
    /// </summary>
    [PostgreSqlFact]
    public async Task The_read_back_refuses_a_capability_of_another_scope_before_any_statement()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Read back");
        f.Host.Actor.Current = f.Writer;
        using var opening = f.Host.Services.CreateScope();
        using var taken = opening.ServiceProvider.GetRequiredService<NHibernate.ISession>().BeginTransaction();
        var edited = (await opening.ServiceProvider.GetRequiredService<Gate>().EditAsync(page.Id, AccessOperation.EditContent, default)).Require();

        // Another scope in a transaction of its own, for an account that may read the page and not edit it.
        f.Host.Actor.Current = f.Reader;
        using var reading = f.Host.Services.CreateScope();
        using var own = reading.ServiceProvider.GetRequiredService<NHibernate.ISession>().BeginTransaction();
        using var stop = new CancellationTokenSource();
        using var recording = SqlRecording.Start();
        var read = reading.ServiceProvider.GetRequiredService<PageReader>().ReadEditedAsync(edited, stop.Token);
        // The refusal comes before the first await. A reader that went on is in a statement by now and would end waiting for
        // the page row the first scope holds: cancelling ends that statement, so this test fails and does not hang.
        await stop.CancelAsync();
        var refusal = await Record.ExceptionAsync(() => read);

        Assert.Empty(recording.Statements);
        Assert.IsType<InvalidOperationException>(refusal);
        // The capability is as good as before where it belongs: only the gate that was asked differs.
        edited.RequireHeld();
    }

    /// <summary>
    /// The read-back on proof stays in the space the gate locked for the page and takes no other. The page instance is the command's to change, so the space it names is no proof of a lock. A command
    /// that moved the page after its gate would otherwise have the reader lock the other space, where nothing decided the
    /// page, and answer from there. As it is, the row the reader finds under its lock is outside the space of the proof, and
    /// that fails as a programming error, not as the 404 of a missing page: the gate locked the row FOR UPDATE in that
    /// space, so only the command itself can have moved it, and a 404 would tell the client that a page it may edit is gone.
    /// </summary>
    [PostgreSqlFact]
    public async Task The_read_back_of_an_edited_page_locks_no_space_but_the_one_its_gate_locked()
    {
        await using var f = await PageGateFixture.CreateAsync();
        var page = await f.PublishedPageAsync("Read back");
        // The Writer may write there too, so the answer comes from the move alone.
        var elsewhere = await f.Host.SeedSpaceAsync("Elsewhere", (f.Writer, SpaceRole.Writer));
        f.Host.Actor.Current = f.Writer;
        using var scope = f.Host.Services.CreateScope();
        // Never committed: the move is rolled back with the scope.
        using var transaction = scope.ServiceProvider.GetRequiredService<NHibernate.ISession>().BeginTransaction();
        var edited = (await scope.ServiceProvider.GetRequiredService<Gate>().EditAsync(page.Id, AccessOperation.EditContent, default)).Require();

        edited.Page.SpaceId = elsewhere;
        using var recording = SqlRecording.Start();
        var refusal = await Record.ExceptionAsync(() => scope.ServiceProvider.GetRequiredService<PageReader>().ReadEditedAsync(edited, default));

        // The steps of every read-back, after the update of the move: no "Space FOR SHARE" of the other space among them.
        Assert.Equal(["update Page", "Page FOR SHARE", "read Page"], LockTrace.Of(recording.Statements));
        Assert.IsType<InvalidOperationException>(refusal);
    }

    /// <summary>As a command does it: the page opened for editing, changed in the session only, then read back in one of the
    /// reader's three ways. The answer and the statements of the read-back alone.</summary>
    private static Task<(PageDto Answer, IReadOnlyList<string> Sent)> ReadBackAsync(PageGateFixture f, UserAccount actor, Guid pageId, string way,
        Action<EditablePage>? change = null)
    {
        return f.InScopeAsync(actor, async services =>
        {
            var operation = change is null ? AccessOperation.EditContent : AccessOperation.ManageAccess;
            var edited = (await services.GetRequiredService<Gate>().EditAsync(pageId, operation, default)).Require();
            change?.Invoke(edited);
            var reader = services.GetRequiredService<PageReader>();
            using var recording = SqlRecording.Start();
            var answer = way switch
            {
                "ReadAsync" => await reader.ReadAsync(pageId, default),
                "ReadEditedAsync" => await reader.ReadEditedAsync(edited, default),
                "ReadEditedWithoutProofAsync" => await reader.ReadEditedWithoutProofAsync(pageId, default),
                _ => throw new ArgumentOutOfRangeException(nameof(way), way, null)
            };
            return (answer, recording.Statements);
        });
    }
}
