using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceSettings.Commands;

public sealed class CreateSpaceHandler
{
    private const int MaxGroupGrants = 100;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;

    public CreateSpaceHandler(ISession session, IContentAccess access, IAtlasLocks locks)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
    }

    public async Task<CreatedSpaceDto> Handle(CreateSpace command, CancellationToken ct)
    {
        var request = command.Request;
        var actor = await m_access.LockActorAsync(null, ct);
        if (!await EffectiveWikiAdmin.IsAdminAsync(m_session, actor, ct))
        {
            throw new ApiException(403, "Prostory vytváří správce wiki.");
        }
        var grants = await ValidateGroupGrantsAsync(request.Groups, ct);
        var appearance = SpaceAppearance.Normalize(request.Icon, request.Color);
        var space = new Space
        {
            Id = Guid.NewGuid(),
            Name = Rules.Required(request.Name, "Název prostoru"),
            Icon = appearance.Icon,
            Color = appearance.Color,
            // The same fields and rules as Nastavení prostoru → Obecné, so the new-space form is that form.
            Description = Rules.OptionalText(request.Description, "Popis prostoru", SaveSpaceSettingsHandler.DescriptionMaxLength) ?? "",
            PinPolicy = request.PinPolicy is null ? SpacePinPolicy.None : SaveSpaceSettingsHandler.ParsePinPolicy(request.PinPolicy)
        };
        await m_session.SaveAsync(space, ct);
        // Every grant of one creation shares one boundary; application clock, not the database default.
        var openedAt = DateTime.UtcNow;
        // Its creator gets the space in the sidebar straight away, whatever the rule for everyone else.
        await m_session.SaveAsync(new SpacePin { Id = Guid.NewGuid(), UserId = actor.Id, SpaceId = space.Id, Pinned = true, ActiveFrom = openedAt }, ct);
        List<Guid> opened = [Guid.NewGuid()];
        await m_session.SaveAsync(new SpaceGrant { Id = opened[0], SpaceId = space.Id, UserId = actor.Id, Role = (int)SpaceRole.Admin, ActiveFrom = openedAt }, ct);
        foreach (var (groupId, role) in grants)
        {
            opened.Add(Guid.NewGuid());
            await m_session.SaveAsync(new SpaceGrant { Id = opened[^1], SpaceId = space.Id, GroupId = groupId, Role = role, ActiveFrom = openedAt }, ct);
        }
        await AccessAuditLog.OpenedAsync(m_session, AccessAuditLog.SpaceGrant, opened, actor.Id, ct);
        return new CreatedSpaceDto(space.Id, space.Name, space.Icon, space.Color, nameof(SpaceRole.Admin), SpaceCoverDto.From(null, space),
            space.Description, space.PinPolicy.ToString());
    }

    private async Task<Dictionary<Guid, int>> ValidateGroupGrantsAsync(IReadOnlyList<CreateSpaceGroupGrant?>? groups, CancellationToken ct)
    {
        if (groups is { Count: > MaxGroupGrants })
        {
            throw new ApiException(400, "Při založení lze přiřadit nejvýše 100 skupin.");
        }
        var grants = new Dictionary<Guid, int>();
        foreach (var group in groups ?? [])
        {
            if (group is null || group.GroupId == Guid.Empty)
            {
                throw new ApiException(400, "Vyberte existující skupinu.");
            }
            var role = group.Role switch
            {
                "Reader" => (int)SpaceRole.Reader,
                "Writer" => (int)SpaceRole.Writer,
                "Admin" => (int)SpaceRole.Admin,
                _ => throw new ApiException(400, "Vyberte roli Reader, Writer nebo Admin.")
            };
            if (!grants.TryAdd(group.GroupId, role))
            {
                throw new ApiException(400, "Každou skupinu lze přiřadit pouze jednou.");
            }
        }
        if (grants.Count == 0)
        {
            return grants;
        }
        // Lock stable identities in one ordered query; deletion cannot race validation and commit.
        var existing = await m_locks.LockGroupsForKeyShareAsync(grants.Keys.ToArray(), ct);
        if (existing.Count != grants.Count)
        {
            throw new ApiException(400, "Některá vybraná skupina již neexistuje. Obnovte seznam skupin.");
        }
        return grants;
    }
}
