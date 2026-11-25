using Atlas.Markdown.Sync;
using NHibernate;
using Atlas.Server.Features.Auth.Domain;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.Auth.Commands;

public sealed class UpdateAppearanceHandler
{
    private readonly ISession m_session;
    private readonly ICurrentActor m_actor;
    private readonly IAtlasLocks m_locks;
    private readonly CollaboratorColors m_colors;

    public UpdateAppearanceHandler(ISession session, ICurrentActor actor, IAtlasLocks locks, CollaboratorColors colors)
    {
        m_colors = colors;
        m_session = session;
        m_actor = actor;
        m_locks = locks;
    }

    public async Task<Appearance> Handle(UpdateAppearance command, CancellationToken ct)
    {
        var actor = await m_actor.RequireAsync(ct);
        Rules.ValidateAppearance(command.Appearance);
        // Serialize only this user's preference updates; other users never wait for this lock.
        if (!await m_locks.LockAccountForUpdateAsync(actor.Id, ct))
        {
            throw new ApiException(401, "Přihlaste se.");
        }
        var current = await m_session.QueryOver<UserAppearancePreference>()
            .Where(x => x.UserId == actor.Id && x.ActiveTo == null)
            .SingleOrDefaultAsync(ct);
        if (current?.Appearance == command.Appearance)
        {
            return command.Appearance;
        }
        var boundary = current is null ? DateTime.UtcNow : Temporal.NextBoundary(current.ActiveFrom, DateTime.UtcNow);
        if (current is not null)
        {
            current.ActiveTo = boundary;
            // Close before inserting so the filtered current index and exclusion constraint remain valid.
            await m_session.FlushAsync(ct);
        }
        await m_session.SaveAsync(new UserAppearancePreference
        {
            UserId = actor.Id,
            Layout = command.Appearance.Layout,
            Accent = command.Appearance.Accent,
            Mode = command.Appearance.Mode,
            Motion = command.Appearance.Motion,
            OpenLocked = command.Appearance.OpenLocked,
            DragPages = command.Appearance.DragPages,
            Language = command.Appearance.Language,
            CollaboratorColor = command.Appearance.CollaboratorColor,
            ActiveFrom = boundary
        }, ct);
        if (current?.CollaboratorColor != command.Appearance.CollaboratorColor)
        {
            // Before the commit: a failed one only moves live colours, it loses nothing. The editor re-announces its awareness at once
            // (live/collaboratorColors.ts), other surfaces with their next presence.
            var slot = CollaboratorPalette.SlotOf(command.Appearance.CollaboratorColor);
            m_colors.Restamp(actor.Id.ToString(), slot >= 0 ? CollaboratorPalette.Entries[slot].Light : null);
        }
        return command.Appearance;
    }
}
