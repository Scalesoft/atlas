using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Atlas.Markdown.Sync;

/// <summary>
/// Periodic projection/snapshot flush for live documents, plus warm-keep eviction.
/// Update-log durability happens synchronously in <see cref="MarkdownSyncHub.SendUpdate"/>.
/// Clones the repo's <c>BackgroundSweepService</c> shape:
/// <c>PeriodicTimer</c> + try/catch-continue + a per-tick activity span. A
/// single failing tick never kills the loop.
/// </summary>
public sealed class DocumentFlushService(
    DocumentRegistry registry,
    IDocumentStore store,
    IHubContext<MarkdownSyncHub> hub,
    DocumentAccessCoordinator accessCoordinator,
    MarkdownSyncOptions options,
    TimeProvider clock,
    MarkdownSyncMetrics metrics,
    ILogger<DocumentFlushService> log) : BackgroundService, IHostedLifecycleService
{
    private static readonly ActivitySource Activity = new("Atlas.Markdown.Sync");

    private readonly record struct FlushOutcome(bool Wrote, Exception? Error);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.FlushInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            using var activity = Activity.StartActivity("tiptap.flush.tick", ActivityKind.Internal);
            var flushed = 0;
            var evicted = 0;
            try
            {
                var outcomes = await FlushManyAsync(registry.All(), stoppingToken).ConfigureAwait(false);
                flushed = outcomes.Count(outcome => outcome.Wrote);

                var now = clock.GetUtcNow();
                foreach (var doc in registry.DueForEviction(now))
                {
                    if (await EvictOneAsync(doc, stoppingToken).ConfigureAwait(false)) evicted++;
                }

                activity?.SetTag("tiptap.flushed", flushed);
                activity?.SetTag("tiptap.evicted", evicted);
                activity?.SetTag("tiptap.active_docs", registry.ActiveDocumentCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                log.LogWarning(ex, "DocumentFlushService tick failed (continuing)");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the periodic sweep first. Otherwise it can race this final CAS loop and evict or
        // flush the same document while shutdown is deciding that persistence is stable.
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // StoppingAsync requested and awaited client snapshots while Kestrel was still accepting hub
        // invocations. Give persistence its own bounded retry window so a slow client request phase
        // cannot consume the time reserved for the final durable CAS.
        var deadline = clock.GetUtcNow() + options.ShutdownSnapshotWait;
        var finalFlushFailureCount = 0;
        var dirtySnapshotsRemain = false;
        while (!cancellationToken.IsCancellationRequested && clock.GetUtcNow() < deadline)
        {
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            using var attemptDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptDeadline.CancelAfter(remaining);
            FlushOutcome[] finalOutcomes;
            try
            {
                finalOutcomes = await FlushManyAsync(registry.All(), attemptDeadline.Token, force: true)
                    .ConfigureAwait(false);
                dirtySnapshotsRemain = await HasDirtySnapshotsAsync(attemptDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (attemptDeadline.IsCancellationRequested)
            {
                break;
            }
            finalFlushFailureCount = finalOutcomes.Count(outcome => outcome.Error is not null);
            if (finalFlushFailureCount == 0 && !dirtySnapshotsRemain) break;

            remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(25)
                    ? remaining
                    : TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        if (finalFlushFailureCount > 0 || dirtySnapshotsRemain)
            log.LogError(
                "Collaborative shutdown ended with {FailureCount} failed flushes; dirty snapshots remain={Dirty}",
                finalFlushFailureCount, dirtySnapshotsRemain);
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        // Graceful shutdown (ADR-018 §3): request snapshots during the lifecycle phase that runs
        // before ordinary hosted services (including the web server) stop accepting messages.
        var deadline = clock.GetUtcNow() + options.ShutdownSnapshotWait;
        var snapshotters = new List<(LiveDocument Document, string ConnId, long Revision)>();
        foreach (var doc in registry.All())
        {
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero ||
                !await doc.Gate.WaitAsync(remaining, cancellationToken).ConfigureAwait(false)) break;
            try
            {
                var snap = doc.SnapshotterConnId;
                if (snap is not null && doc.PeerCount > 0)
                    snapshotters.Add((doc, snap, doc.SnapshotRevision));
            }
            finally { doc.Gate.Release(); }
        }

        var snapshotRequestFailures = 0;
        foreach (var (document, connId, _) in snapshotters)
        {
            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            try
            {
                await hub.Clients.Client(connId)
                    .SendAsync(MarkdownSyncHub.RequestSnapshot, document.DocumentId, cancellationToken)
                    .WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (TimeoutException)
            {
                snapshotRequestFailures++;
                break;
            }
            catch (Exception ex)
            {
                    snapshotRequestFailures++;
                log.LogDebug(ex, "Final snapshot request failed for {DocumentId}; client may already be gone",
                    document.DocumentId);
            }
        }

        if (snapshotRequestFailures > 0)
            log.LogWarning("{FailureCount} final snapshot requests could not be delivered", snapshotRequestFailures);

        if (snapshotters.Count > 0 && !cancellationToken.IsCancellationRequested && clock.GetUtcNow() < deadline)
            await WaitForRequestedSnapshotsAsync(snapshotters, deadline, cancellationToken).ConfigureAwait(false);
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task WaitForRequestedSnapshotsAsync(
        IReadOnlyList<(LiveDocument Document, string ConnId, long Revision)> requests,
        DateTimeOffset deadline,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && clock.GetUtcNow() < deadline)
        {
            var awaiting = false;
            foreach (var (doc, connId, revision) in requests)
            {
                var remaining = deadline - clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero ||
                    !await doc.Gate.WaitAsync(remaining, ct).ConfigureAwait(false)) return;
                try
                {
                    if (doc.SnapshotRevision <= revision && doc.SnapshotterConnId == connId &&
                        doc.Peers.ContainsKey(connId)) awaiting = true;
                }
                finally { doc.Gate.Release(); }
            }
            if (!awaiting) return;
            var delayRemaining = deadline - clock.GetUtcNow();
            if (delayRemaining <= TimeSpan.Zero) return;
            await Task.Delay(delayRemaining < TimeSpan.FromMilliseconds(25)
                    ? delayRemaining
                    : TimeSpan.FromMilliseconds(25), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<bool> HasDirtySnapshotsAsync(CancellationToken ct)
    {
        foreach (var doc in registry.All())
        {
            await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
            try { if (doc.SnapshotDirty) return true; }
            finally { doc.Gate.Release(); }
        }
        return false;
    }

    /// <summary>Flush a single document's pending snapshot and retain the persistence failure for callers.</summary>
    private async Task<FlushOutcome> FlushOneAsync(LiveDocument doc, CancellationToken ct, bool force = false)
    {
        bool degradedToggledOn = false, recovered = false, wrote = false, externalArchived = false;
        Exception? persistenceError = null;
        DocSnapshot? pendingSnapshot = null;
        byte[]? capturedState = null;
        bool suppressExternalReport = false;
        await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (doc.AuthorityEpoch is not null) return new FlushOutcome(false, null);
            var hasWork = doc.SnapshotDirty;
            if (!hasWork && !force) return new FlushOutcome(false, null);
            if (doc.SnapshotDirty && doc.Snapshot is not null && doc.LastProjection is not null)
            {
                capturedState = doc.Snapshot;
                pendingSnapshot = SnapshotValidation.Prepare(new DocSnapshot(capturedState, doc.SnapshotSeq, doc.LastProjection));
                suppressExternalReport = doc.SuppressNextExternalEditReport;
            }
        }
        finally
        {
            doc.Gate.Release();
        }

        var flushStart = Stopwatch.GetTimestamp();
        FlushResult result = FlushResult.None;
        try
        {
            if (pendingSnapshot is not null)
            {
                result = await store.FlushAsync(doc.DocumentId, pendingSnapshot, ct).ConfigureAwait(false);
                wrote = true;
            }
        }
        catch (Exception ex)
        {
            persistenceError = ex;
            log.LogError(ex, "Flush failed for {DocumentId} — keeping dirty, retrying next tick", doc.DocumentId);
        }

        // Once store I/O has completed, always reconcile the captured result with live state.
        await doc.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A V1 flush captured before cutover cannot change V2 projection health/watermarks.
            if (doc.AuthorityEpoch is not null) return new FlushOutcome(false, null);
            if (persistenceError is null)
            {
                if (result.IsSuperseded)
                {
                    wrote = false;
                    log.LogInformation("Collab doc {DocumentId} was replaced in the store; its pending snapshot is dropped", doc.DocumentId);
                }
                if (pendingSnapshot is not null && ReferenceEquals(doc.Snapshot, capturedState)
                                                && doc.SnapshotSeq == pendingSnapshot.CoveredSequence)
                    doc.SnapshotDirty = false;
                if (pendingSnapshot is not null && suppressExternalReport)
                    doc.SuppressNextExternalEditReport = false;
                externalArchived = result.ExternalEditArchived && !suppressExternalReport;
                if (doc.FlushDegraded)
                {
                    doc.FlushDegraded = false;
                    recovered = true;
                }
            }
            else if (!doc.FlushDegraded)
            {
                doc.FlushDegraded = true;
                degradedToggledOn = true;
            }
        }
        finally
        {
            doc.Gate.Release();
        }
        metrics.RecordFlush(Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds, persistenceError is null);

        if (degradedToggledOn)
        {
            log.LogWarning("Collab doc {DocumentId} flush degraded — disk writes failing, sync continues in memory",
                doc.DocumentId);
            await accessCoordinator.SendToReadersAsync(doc, MarkdownSyncHub.FlushDegraded,
                [doc.DocumentId, true], ct: ct).ConfigureAwait(false);
        }
        else if (recovered)
        {
            log.LogInformation("Collab doc {DocumentId} flush recovered — disk writes succeeding again",
                doc.DocumentId);
            await accessCoordinator.SendToReadersAsync(doc, MarkdownSyncHub.FlushDegraded,
                [doc.DocumentId, false], ct: ct).ConfigureAwait(false);
        }

        if (externalArchived)
        {
            // ADR-018 §5 — an external edit was archived before the projection overwrote it.
            // Tell participants (CZ toast) that the file was reconciled; never log content.
            log.LogInformation("Collab doc {DocumentId} external edit archived before flush — live session won",
                doc.DocumentId);
            await accessCoordinator.SendToReadersAsync(doc, MarkdownSyncHub.ExternalEditArchived,
                [doc.DocumentId], ct: ct).ConfigureAwait(false);
        }

        if (wrote && persistenceError is null && pendingSnapshot is not null)
            await MarkdownSyncHub.AnnounceSnapshotStoredAsync(accessCoordinator, log, doc, pendingSnapshot.CoveredSequence).ConfigureAwait(false);

        return new FlushOutcome(wrote, persistenceError);
    }

    private async Task<FlushOutcome[]> FlushManyAsync(
        IEnumerable<LiveDocument> documents,
        CancellationToken ct,
        bool force = false)
    {
        using var concurrency = new SemaphoreSlim(4, 4);
        var tasks = documents.ToArray().Select(async document =>
        {
            await concurrency.WaitAsync(ct).ConfigureAwait(false);
            try { return await FlushOneAsync(document, ct, force).ConfigureAwait(false); }
            finally { concurrency.Release(); }
        });
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>Final-flush and evict a warm document whose keep window elapsed. Returns true if evicted.</summary>
    private async Task<bool> EvictOneAsync(LiveDocument doc, CancellationToken ct)
    {
        // Final flush under the gate, then evict only if still peerless (a rejoin cancels eviction).
        var outcome = await FlushOneAsync(doc, ct, force: true).ConfigureAwait(false);
        if (outcome.Error is not null) return false;

        await doc.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (doc.PeerCount > 0)
            {
                registry.ClearWarm(doc);
                return false;
            }

            if (!registry.IsDueForEviction(doc, clock.GetUtcNow())) return false;
            if (doc.SnapshotDirty) return false;
            // Compare-and-remove: only evict if this is still the registered instance, so a
            // concurrent join that already re-registered a fresh shell isn't orphaned.
            if (!registry.EvictIfCurrent(doc)) return false;
            log.LogInformation("Collab doc {DocumentId} evicted after warm-keep", doc.DocumentId);
            return true;
        }
        finally
        {
            doc.Gate.Release();
        }
    }
}
