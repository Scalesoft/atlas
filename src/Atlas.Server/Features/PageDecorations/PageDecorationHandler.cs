using Atlas.Server.Features.PageDecorations.Domain;
using Atlas.Server.Features.PageOrganization.Domain;
using Atlas.Server.Features.PersonalLibrary.Domain;
using Atlas.Server.Platform.Validation;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.PageDecorations;

public sealed class PageDecorationHandler(ISession session, PersonalPageGuard guard, OrganizationLocks locks, PageDecorationReader reader)
{
    // Spaces and page icons share one catalog of icons and colours.
    private static IReadOnlySet<string> Icons => AppearanceCatalog.Icons;
    private static IReadOnlySet<string> Colors => AppearanceCatalog.Colors;

    public async Task<PageDecorationState> Handle(GetPageDecoration query, CancellationToken ct)
    {
        await guard.RequirePageAsync(query.PageId, false, ct);
        return new(await reader.ReadAsync(query.PageId, ct));
    }

    public async Task<PageDecorationState> Handle(SetPageDecoration command, CancellationToken ct)
    {
        // A readable page without EditContent answers 403, anything else 404.
        await locks.LockPageAsync(command.PageId, false, AccessOperation.EditContent, ct);
        var request = command.Request;
        if ((request.Icon is null) != (request.Color is null)
            || request.Icon is not null && (!Icons.Contains(request.Icon) || !Colors.Contains(request.Color!)))
            throw new ApiException(400, "Vyberte podporovanou ikonu a barvu.");

        var current = await session.GetAsync<PageDecoration>(command.PageId, ct);
        if (current?.RevisionId != request.ExpectedRevisionId)
            throw new ApiException(409, "Ikona stránky byla změněna. Načtěte ji znovu.", "decoration_changed");
        if (current?.Icon == request.Icon && current?.Color == request.Color)
            return new(current is null ? null : PageDecorationReader.Describe(current));

        if (current is null)
        {
            current = new PageDecoration
            {
                PageId = command.PageId, RevisionId = Guid.CreateVersion7(), Icon = request.Icon, Color = request.Color
            };
            await session.SaveAsync(current, ct);
        }
        else
        {
            current.Icon = request.Icon;
            current.Color = request.Color;
            current.RevisionId = Guid.CreateVersion7();
        }
        await session.FlushAsync(ct);
        return new(PageDecorationReader.Describe(current));
    }
}
