using Atlas.Server.Features.Access.Domain;

namespace Atlas.Server.Features.Access.Commands;

/// <summary>The generic grant endpoints: the subject and level are passed to the kind's writer, which calls the kind's existing
/// command handler in this transaction (validation, authority, fresh sign-in, CAS, audit and AccessChanges stay there).</summary>
public sealed class AccessGrantHandler
{
    private readonly IEnumerable<IAccessKind> m_kinds;

    public AccessGrantHandler(IEnumerable<IAccessKind> kinds)
    {
        m_kinds = kinds;
    }

    public async Task Handle(SetAccessGrant command, CancellationToken ct)
    {
        var request = command.Request;
        var (userId, groupId) = Subject(request.SubjectKind, request.SubjectId);
        if (request.Level is null)
        {
            throw new ApiException(400, "Vyberte úroveň přístupu.");
        }
        var writer = await WriterAsync(command.Kind, ct);
        await writer.ApplyAsync(command.ResourceId, new AccessGrantChange(userId, groupId, request.Level, request.StartAt, request.ExpiresAt,
            request.ExpectedVersion), ct);
    }

    public async Task Handle(RevokeAccessGrant command, CancellationToken ct)
    {
        var (userId, groupId) = Subject(command.SubjectKind, command.SubjectId);
        var writer = await WriterAsync(command.Kind, ct);
        await writer.ApplyAsync(command.ResourceId, new AccessGrantChange(userId, groupId, null, null, null, command.Version), ct);
    }

    private async Task<IAccessGrantWriter> WriterAsync(string kindId, CancellationToken ct)
    {
        var kind = m_kinds.FirstOrDefault(k => k.Descriptor.Id == kindId);
        if (kind is null || !await kind.IsEnabledAsync(ct))
        {
            throw new ApiException(404, "Druh přístupu nebyl nalezen.");
        }
        return kind.Writer ?? throw new ApiException(400, "Tento druh přístupu se nepřiděluje.");
    }

    private static (Guid? UserId, Guid? GroupId) Subject(string? kind, Guid? id)
    {
        return (kind, id) switch
        {
            (AccessPrincipal.User, { } user) => (user, null),
            (AccessPrincipal.Group, { } group) => (null, group),
            _ => throw new ApiException(400, "Vyberte právě jednoho uživatele nebo skupinu.")
        };
    }
}
