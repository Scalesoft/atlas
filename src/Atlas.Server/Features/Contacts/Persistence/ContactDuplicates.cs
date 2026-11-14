using Atlas.Server.Features.Contacts.Domain;
using Atlas.Server.Platform.Objects;
using Atlas.Server.Platform.Resources;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Contacts.Persistence;

/// <summary>
/// One contact per e-mail address within a scope (the wiki; a space; one owner's private contacts): a new contact whose address a
/// live contact of the scope has is refused with that contact's id. Only the scope, so the answer never reveals a contact its
/// creator may not read. Creations in one scope serialize on an advisory lock, so two people adding the same person at once do not
/// both pass.
/// </summary>
public sealed class ContactDuplicates
{
    private readonly ISession m_session;
    private readonly IAtlasLocks m_locks;

    public ContactDuplicates(ISession session, IAtlasLocks locks)
    {
        m_session = session;
        m_locks = locks;
    }

    public async Task RefuseAsync(ObjectChangeContext context, IReadOnlyList<string> emailKeys, CancellationToken ct)
    {
        if (emailKeys.Count == 0)
        {
            return;
        }
        // The scope's partition: its space, a private contact's owner, the whole wiki.
        var partition = context.SpaceId ?? (context.Scope == ObjectScopes.Private ? context.Actor.Account.Id : Guid.Empty);
        await m_locks.AcquireAdvisoryAsync($"{ContactObjectType.Id}:{context.Scope}:{partition}", ct);
        var existing = await m_session.CreateSQLQuery("""
                SELECT r."Id" FROM "ContentResource" r
                WHERE r."TypeId" = :type AND r."TrashedAt" IS NULL AND r."Scope" = :scope
                  AND COALESCE(r."SpaceId", CASE WHEN r."Scope" = 'private' THEN r."OwnerId" END, '00000000-0000-0000-0000-000000000000') = :partition
                  AND EXISTS (SELECT 1 FROM jsonb_array_elements_text(r."Data" -> 'emails') email WHERE lower(email) IN (:emails))
                ORDER BY r."CreatedAt", r."Id"
                LIMIT 1
                """)
            .AddScalar("Id", NHibernateUtil.Guid)
            .SetParameter("type", ContactObjectType.Id)
            .SetParameter("scope", context.Scope)
            .SetParameter("partition", partition)
            .SetParameterList("emails", emailKeys.ToArray())
            .UniqueResultAsync<Guid?>(ct);
        if (existing is { } id)
        {
            throw ContactErrors.Duplicate(id);
        }
    }
}
