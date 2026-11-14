using NHibernate;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Domain;
using Atlas.Server.Features.SpaceSettings.Queries;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.SpaceSettings.Commands;

public sealed class SaveSpaceSettingsHandler
{
    internal const int DescriptionMaxLength = 2000;
    private readonly ISession m_session;
    private readonly IContentAccess m_access;
    private readonly IAtlasLocks m_locks;
    private readonly AccessChanges m_accessChanges;

    public SaveSpaceSettingsHandler(ISession session, IContentAccess access, IAtlasLocks locks, AccessChanges accessChanges)
    {
        m_session = session;
        m_access = access;
        m_locks = locks;
        m_accessChanges = accessChanges;
    }

    public async Task<SpaceSettingsDto> Handle(SaveSpaceSettings command, CancellationToken ct)
    {
        var request = command.Request;
        // Same lock order as grants/organization: global membership, space, current actor.
        // Acquire UPDATE directly: upgrading a shared space lock can deadlock two editors.
        await m_locks.AcquireContentSharedAsync(ct);
        var space = await LiveSpace.FindAsync(m_session, command.SpaceId, ct, LockMode.Upgrade)
            ?? throw new ApiException(404, "Prostor nebyl nalezen nebo k němu nemáte přístup.");
        await m_access.LockActorAsync(null, ct);
        await m_access.RequireSpaceAsync(command.SpaceId, SpaceRole.Admin, ct);
        var current = await CurrentSettings.LoadAsync(m_session, command.SpaceId, ct);
        if (request.ExpectedVersion != current.ActiveFrom)
        {
            throw new ApiException(409, "Nastavení prostoru mezitím změnil jiný správce. Načtěte aktuální nastavení; vaše úpravy zůstávají ve formuláři.", "space_settings_conflict");
        }
        var name = Rules.Required(request.Name, "Název prostoru", unchanged: current.Name);
        var appearance = SpaceAppearance.Validate(request.Icon, request.Color);
        var description = Rules.OptionalText(request.Description, "Popis prostoru", DescriptionMaxLength, current.Description) ?? "";
        var pinPolicy = ParsePinPolicy(request.PinPolicy);
        space.Name = name;
        space.Icon = appearance.Icon;
        space.Color = appearance.Color;
        space.Description = description;
        space.PinPolicy = pinPolicy;
        space.CommentsEnabled = request.CommentsEnabled ?? space.CommentsEnabled;
        var mode = request.PublicationMode is null ? space.PublicationMode : PublicationModes.Parse(request.PublicationMode);
        // Živě decides who reads the live documents: their readers are re-authorized at the commit (AccessChanges).
        if (mode != space.PublicationMode) m_accessChanges.Changed();
        space.PublicationMode = mode;
        // Flush now so the metadata revision is written (ContentRowEffects) before we read it back.
        await m_session.FlushAsync(ct);
        return await CurrentSettings.ReadAsync(m_session, command.SpaceId, ct);
    }

    internal static SpacePinPolicy ParsePinPolicy(string? value) => value switch
    {
        nameof(SpacePinPolicy.None) => SpacePinPolicy.None,
        nameof(SpacePinPolicy.Default) => SpacePinPolicy.Default,
        nameof(SpacePinPolicy.Required) => SpacePinPolicy.Required,
        _ => throw new ApiException(400, "Vyberte, zda je prostor nepřipnutý, připnutý všem, nebo povinně připnutý.")
    };
}
