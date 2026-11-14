using System.Text.Json;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Collaboration.Contracts;
using Atlas.Server.Features.Collaboration.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Collaboration.Commands;

public sealed class BindDocumentSchemaHandler
{
    private readonly ISession m_session;

    public BindDocumentSchemaHandler(ISession session)
    {
        m_session = session;
    }

    public async Task<BoundSchema> Handle(BindDocumentSchema command, CancellationToken ct)
    {
        CollaborationSchema.RequireAcceptable(command.Schema);
        var state = await LiveDocument.LockAsync(m_session, command.PageId, command.Generation, ct);
        if (state.SchemaBound)
        {
            DocumentSchemaCompatibility.EnsureCompatible(CollaborationSchema.Deserialize(state.SchemaJson), command.Schema);
        }
        else
        {
            if (state.Sequence != 0 || state.StateSha256 is not null)
            {
                DocumentSchemaCompatibility.EnsureCompatible(null, command.Schema);
            }
            state.SchemaBound = true;
            state.SchemaJson = command.Schema is null ? null : JsonSerializer.Serialize(command.Schema);
            state.UpdatedAt = DateTime.UtcNow;
        }
        return new BoundSchema(command.Schema);
    }
}
