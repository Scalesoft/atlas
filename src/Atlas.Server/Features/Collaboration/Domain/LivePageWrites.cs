using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Wolverine;

namespace Atlas.Server.Features.Collaboration.Domain;

/// <summary>The server's document authority for live documents (3.6-H; <see cref="ManagedLiveWriteAuthority"/>).</summary>
public interface ILiveWriteAuthority
{
    /// <summary>The snapshot (state and projections) of the live document at its head.</summary>
    Task<DocSnapshot> CheckpointAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, long sequence, CancellationToken ct);

    /// <summary>The update turning the live document into <paramref name="target"/> (the rich document's <c>doc</c> node).</summary>
    Task<byte[]> WriteAsync(MarkdownDocumentSchema schema, byte[] snapshot, IReadOnlyList<byte[]> updates, JsonElement target, long sequence, CancellationToken ct);

    /// <summary>The initial live state of a stored document (the rich document's <c>doc</c> node), as the browser's seed builds it (LiveSeeds).</summary>
    Task<DocSnapshot> SeedAsync(MarkdownDocumentSchema schema, JsonElement document, CancellationToken ct);
}

/// <summary>
/// 3.6-H: an agent's content write (MCP, the personal API, the assistant) into a page open in live editing arrives as a collaborator's
/// update, not as a new generation that remounts every editor and is refused while anyone types. Around the writer's own command:
/// <list type="number">
/// <item>The page's document gate is taken first (then the writer's database locks, the hub's order), so nothing changes the live document meanwhile.</item>
/// <item>Unsaved edits of people are checkpointed by the server as their own revision (author: who typed last, channel web): the agent's
/// revision then holds only the agent's change, and history can give it a version of its own (3.6-C rule 5).</item>
/// <item>The writer runs unchanged on that revision; the version its agent read is accepted (<c>LiveWriteBase</c>) and its target checked
/// instead (<see cref="LiveWriteTarget"/>). Its save keeps the generation (<see cref="CollaborationCheckpoints.ResetAsync"/>).</item>
/// <item>After the commit the document authority turns the revision into one Yjs update of the live document, appended through the content
/// guard and relayed like a browser's, with its origin (channel, token, conversation); its sequence is the revision's checkpoint.</item>
/// </list>
/// The agent shows among the page's collaborators meanwhile. Without live editing (no peer, nothing unsaved), or
/// when the update cannot be made, the write is today's: a new revision and a new generation; nothing typed is lost either way, as the
/// people's edits were checkpointed under the same gate.
/// </summary>
public sealed class LivePageWrites(IMessageBus bus, LiveDocumentWriter writer, IServiceProvider services, IServiceScopeFactory scopes,
    ICurrentActor actor, IRequestChannel channel, ILogger<LivePageWrites> log)
{
    /// <summary>Every plugin of the host, at its schema revision: the schema of a live document that has not bound one.</summary>
    private static readonly MarkdownDocumentSchema HostSchema = new(HostDocumentValidator.Instance.Schema.SchemaRevision, HostDocumentValidator.Instance.Schema.Profile,
        HostDocumentValidator.Instance.Schema.Plugins.Values.Select(plugin => new MarkdownSchemaPlugin(plugin.Id, plugin.Version)).ToArray());

    /// <param name="readVersion">The version the agent read (its expectedVersion), or null when the write names none.</param>
    /// <param name="wholeDocument">The write replaces the whole document: it needs the whole document unchanged since the read.</param>
    /// <param name="mcp">An MCP tool's write: a space out of AI is no live write either (its handler answers the 404).</param>
    public async Task<T> RunAsync<T>(Guid pageId, int? readVersion, bool wholeDocument, Func<Task<T>> write, CancellationToken ct, bool mcp = false)
    {
        // A write the actor may not make goes straight to its handler's refusal: no presence, no gate, no checkpoint of people's edits.
        if (LivePageWriteScope.Current is not null || services.GetService<ILiveWriteAuthority>() is not { } authority
            || (await bus.InvokeAsync<LiveWritePageResult>(new GetLiveWritePage(pageId, mcp), ct)).Page is not { } before)
        {
            return await write();
        }
        var documentId = new WikiDocumentId(pageId, before.Generation).ToString();
        var claimed = actor.Claimed().Id;
        var current = channel.Current;
        var agent = await bus.InvokeAsync<LiveWriteAgent>(new GetLiveWriteAgent(claimed, current.ApiTokenId), ct);
        // An agent shows among the collaborators while it writes; a person's own web action (linking a page) needs no second avatar.
        long? presence = current.Channel is ContentChannel.Mcp or ContentChannel.Api or ContentChannel.Assistant ? Random.Shared.NextInt64(1, int.MaxValue) : null;
        if (presence is { } shown)
        {
            await writer.ShowAsync(documentId, shown, 1, new CollabUser($"ai:{claimed:N}", $"{agent.Name} · AI ({Source(current, agent.TokenName)})"), ct);
        }
        try
        {
            return await HeldAsync(pageId, documentId, readVersion, wholeDocument, mcp, write, authority, new CollabUser(claimed.ToString(), agent.Name),
                new UpdateOrigin(current.Code, current.ApiTokenId, current.ConversationId), ct);
        }
        finally
        {
            if (presence is { } hidden)
            {
                await writer.ShowAsync(documentId, hidden, 2, null, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The server's own checkpoint of a live page (Commands.LiveCheckpoints), as the actor, a person who typed in it: under the document
    /// gate, what people typed becomes their revision as before an agent's write. Nothing when <paramref name="generation"/> is no longer
    /// the page's or nothing is unsaved (a browser's or another checkpoint saved it meanwhile).
    /// </summary>
    /// <returns>False when the actor may not edit the page (nothing was done): the caller asks the next person of the tail.</returns>
    public async Task<bool> CheckpointAsync(Guid pageId, Guid generation, CancellationToken ct)
    {
        if (services.GetService<ILiveWriteAuthority>() is not { } authority)
        {
            return true;
        }
        if ((await bus.InvokeAsync<LiveWritePageResult>(new GetLiveWritePage(pageId), ct)).Page is null)
        {
            return false;
        }
        await using var hold = await writer.HoldAsync(new WikiDocumentId(pageId, generation).ToString(), ct);
        var page = hold is null ? null : (await bus.InvokeAsync<LiveWritePageResult>(new GetLiveWritePage(pageId), ct)).Page;
        if (hold is null || page is null || page.Generation != generation || page.Sequence != hold.Sequence || page.LastSavedSequence >= page.Sequence)
        {
            return true;
        }
        await CheckpointPeopleAsync(hold, hold.Schema ?? HostSchema, pageId, page, authority, ct, actor.Claimed().Id);
        return true;
    }

    private async Task<T> HeldAsync<T>(Guid pageId, string documentId, int? readVersion, bool wholeDocument, bool mcp, Func<Task<T>> write,
        ILiveWriteAuthority authority, CollabUser author, UpdateOrigin origin, CancellationToken ct)
    {
        await using var hold = await writer.HoldAsync(documentId, ct);
        var page = hold is null ? null : (await bus.InvokeAsync<LiveWritePageResult>(new GetLiveWritePage(pageId, mcp), ct)).Page;
        if (hold is null || page is null || new WikiDocumentId(pageId, page.Generation).ToString() != documentId || page.Sequence != hold.Sequence
            || (hold.PeerCount == 0 && page.LastSavedSequence >= page.Sequence))
        {
            // Not live (never seeded, nobody here and nothing unsaved) or replaced meanwhile: today's write.
            if (hold is not null) await hold.DisposeAsync();
            return await write();
        }
        var schema = hold.Schema ?? HostSchema;
        if (page.LastSavedSequence < page.Sequence)
        {
            await CheckpointPeopleAsync(hold, schema, pageId, page, authority, ct);
            page = (await bus.InvokeAsync<LiveWritePageResult>(new GetLiveWritePage(pageId, mcp), ct)).Page!;
        }
        // A version above the current one is no read of this page: the writer refuses it as a conflict.
        var read = readVersion is { } version && version <= page.Version ? version : page.Version;
        var scope = new LivePageWriteScope(pageId, page.Generation, read, page.Version, page.RevisionId, wholeDocument);
        T result;
        try
        {
            using (LivePageWriteScope.Use(scope))
            using (LiveWriteBase.Use(read, page.Version))
            {
                result = await write();
            }
        }
        catch
        {
            // A revision committed before the failure must still reach the live document.
            if (scope.Written is { } written
                && (await bus.InvokeAsync<LiveWriteRevision>(new LiveWriteRevisionExists(written.Id), CancellationToken.None)).Exists)
            {
                await AppendAsync(hold, schema, scope, authority, author, origin);
            }
            throw;
        }
        if (scope.Written is not null)
        {
            await AppendAsync(hold, schema, scope, authority, author, origin);
        }
        return result;
    }

    /// <summary>The people's unsaved edits as their revision, as their browser's autosave would store it: the snapshot of the head, then
    /// the page's checkpoint save as the author of the newest unsaved update on the channel web, in a flow of its own (no request).</summary>
    private async Task CheckpointPeopleAsync(LiveDocumentHold hold, MarkdownDocumentSchema schema, Guid pageId, LiveWritePage page,
        ILiveWriteAuthority authority, CancellationToken ct, Guid? author = null)
    {
        // Before the snapshot is adopted: adopting it drops the updates it covers.
        var person = author ?? (Guid.TryParse(hold.AuthorAfter(page.LastSavedSequence), out var id) ? id : actor.Claimed().Id);
        DocSnapshot snapshot;
        try
        {
            snapshot = await authority.CheckpointAsync(schema, hold.Snapshot, hold.Updates, hold.Sequence, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // People's edits that cannot be read as a document stay as they are: the agent's write waits for their save.
            log.LogWarning(error, "The server could not checkpoint the unsaved live edits of page {PageId} before a live write", pageId);
            throw new ApiException(409, CollaborationCheckpoints.UnsavedMessage, "collaboration_unsaved");
        }
        await hold.AdoptAsync(snapshot, ct);
        var stamp = (await bus.InvokeAsync<LiveWriteAgent>(new GetLiveWriteAgent(person, null), ct)).SecurityStamp;
        var content = $"{{\"format\":\"{MarkdownDocumentValidator.Format}\",\"version\":1,\"plugins\":{FlushDocumentHandler.Plugins},\"document\":{snapshot.Projections.EditorJson.GetRawText()}}}";
        var save = new SavePage(pageId, new SavePageRequest(page.Title, content, null, page.Version, page.Generation));
        Task saved;
        using (ExecutionContext.SuppressFlow())
        {
            saved = Task.Run(async () =>
            {
                using var acting = ActingAccount.Use(person, stamp, RequestChannel.Web);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<PageDto>(save, ct);
            }, ct);
        }
        try
        {
            await saved;
        }
        catch (ApiException error)
        {
            log.LogInformation("The server checkpoint before a live write of page {PageId} was refused ({Code})", pageId, error.Code);
            throw new ApiException(409, CollaborationCheckpoints.UnsavedMessage, "collaboration_unsaved");
        }
    }

    /// <summary>After the writer's commit: the revision as one update of the live document, or today's reset when that cannot be done.</summary>
    private async Task AppendAsync(LiveDocumentHold hold, MarkdownDocumentSchema schema, LivePageWriteScope scope, ILiveWriteAuthority authority,
        CollabUser author, UpdateOrigin origin)
    {
        var written = scope.Written!.Value;
        try
        {
            using var content = JsonDocument.Parse(written.ContentJson, MarkdownDocumentValidator.JsonOptions);
            var update = await authority.WriteAsync(schema, hold.Snapshot, hold.Updates, content.RootElement.GetProperty("document"), hold.Sequence + 1,
                CancellationToken.None);
            var sequence = await hold.AppendAsync(author, update, origin, CancellationToken.None);
            await bus.InvokeAsync(new MarkLiveWriteSaved(scope.PageId, scope.Generation, sequence, written.Id),CancellationToken.None);
        }
        catch (Exception error)
        {
            // Nobody has unsaved edits (checkpointed above, under this gate), so the reset loses nothing; the editors remount on the revision.
            log.LogWarning(error, "Live write of page {PageId} could not be appended; the generation is reset to its revision", scope.PageId);
            await bus.InvokeAsync(new ResetLiveWrite(scope.PageId, written.Id, scope.Generation), CancellationToken.None);
        }
    }

    private static string Source(RequestChannel channel, string? token) => channel.Channel switch
    {
        ContentChannel.Mcp => token is null ? "MCP" : $"MCP: {token}",
        ContentChannel.Api => token is null ? "API" : $"API: {token}",
        _ => "Asistent",
    };
}
