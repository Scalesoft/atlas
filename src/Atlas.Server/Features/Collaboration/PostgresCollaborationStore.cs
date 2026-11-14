using System.Text;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Commands;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using Atlas.Server.Features.Collaboration.Queries;
using Wolverine;

namespace Atlas.Server.Features.Collaboration;

/// <summary>Single-host relay persistence. The relay has no DI scope of its own, so every operation runs one message in a fresh scope and transaction.</summary>
public sealed class PostgresCollaborationStore : IDocumentStore
{
    private const int MaxStateBytes = 4 * 1024 * 1024;
    private readonly IServiceScopeFactory m_scopes;

    public PostgresCollaborationStore(IServiceScopeFactory scopes)
    {
        m_scopes = scopes;
    }

    public async Task<StoredDocument?> LoadAsync(string documentId, CancellationToken ct)
    {
        var id = WikiDocumentId.Parse(documentId);
        return await InvokeAsync<StoredDocument>(new LoadDocument(id.PageId, id.Generation), ct);
    }

    public async Task<MarkdownDocumentSchema?> BindSchemaAsync(string documentId, MarkdownDocumentSchema? schema, CancellationToken ct)
    {
        var id = WikiDocumentId.Parse(documentId);
        var requested = schema is null ? null : DocumentSchemaCompatibility.Normalize(schema);
        var bound = await InvokeAsync<BoundSchema>(new BindDocumentSchema(id.PageId, id.Generation, requested), ct);
        return bound.Schema;
    }

    public Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId,
        DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update, CancellationToken ct)
        => AppendUpdateAsync(documentId, clientUpdateId, userId, receivedAtUtc, update, null, ct);

    public async Task<AppendUpdateResult> AppendUpdateAsync(string documentId, Guid clientUpdateId, string userId,
        DateTimeOffset receivedAtUtc, ReadOnlyMemory<byte> update, UpdateOrigin? origin, CancellationToken ct)
    {
        var id = WikiDocumentId.Parse(documentId);
        if (clientUpdateId == Guid.Empty || !Guid.TryParse(userId, out var authorId))
        {
            throw new ArgumentException("Invalid update or author identifier.");
        }
        if (update.Length is < 1 or > MaxStateBytes)
        {
            throw new InvalidDataException("Update size is outside the allowed range.");
        }
        try
        {
            return await InvokeAsync<AppendUpdateResult>(new AppendUpdate(id.PageId, id.Generation, clientUpdateId, authorId, receivedAtUtc, update.ToArray(), origin), ct);
        }
        catch (ApiException error) when (error.Code == CollaborationErrors.SupersededCode)
        {
            throw new DocumentSupersededException(error.Message);
        }
    }

    public async Task LastPeerLeftAsync(string documentId, CancellationToken ct)
    {
        var id = WikiDocumentId.Parse(documentId);
        await InvokeAsync(new RequestLiveCheckpoint(id.PageId, id.Generation), ct);
    }

    public async Task<FlushResult> FlushAsync(string documentId, DocSnapshot snapshot, CancellationToken ct)
    {
        var id = WikiDocumentId.Parse(documentId);
        snapshot = SnapshotValidation.Prepare(snapshot);
        // References must keep the plugin contract before anything is read from the database. FlushDocument then validates the
        // projection as a document (its references against what the guard admitted) and stores the server's Markdown: the
        // client's Markdown and plain text are never stored.
        SecretReferenceContent.Collect(snapshot.Projections.EditorJson);
        if (snapshot.YjsState.Length > MaxStateBytes || Encoding.UTF8.GetByteCount(snapshot.Projections.EditorJson.GetRawText()) > MaxStateBytes
            || Encoding.UTF8.GetByteCount(snapshot.Projections.Markdown) > MaxStateBytes
            || Encoding.UTF8.GetByteCount(snapshot.Projections.PlainText) > MaxStateBytes)
        {
            throw new InvalidDataException("Snapshot exceeds the configured document size bound.");
        }
        return await InvokeAsync<FlushResult>(new FlushDocument(id.PageId, id.Generation, snapshot), ct);
    }

    public async Task<bool> HasExternalChangeAsync(string documentId, CancellationToken ct)
    {
        var id = WikiDocumentId.Parse(documentId);
        var result = await InvokeAsync<ExternalChange>(new HasExternalChange(id.PageId, id.Generation), ct);
        return result.Changed;
    }

    private async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }

    private async Task InvokeAsync(object message, CancellationToken ct)
    {
        await using var scope = m_scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync(message, ct);
    }
}
