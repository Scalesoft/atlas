using System.Reflection;
using NHibernate;
using Atlas.Markdown.Sync;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Features.KnowledgeMaintenance.Queries;

/// <summary>
/// The pages of a space whose stored working or published revision the pinned editor would refuse to open
/// (the editor package's <see cref="MarkdownDocumentValidator"/>), with the first violation of each. Writes are validated since the editor schema
/// check exists; this finds what was stored before (or by a path that bypassed it) so a space administrator can repair it in
/// the editor's source view or through the API. A working title with a line break, control or bidirectional character
/// (stored before <see cref="Rules.Required"/> refused them) is listed too, with the path <c>title</c>. Read-only: nothing is rewritten.
/// </summary>
/// <param name="Cursor">The <see cref="ContentValidationReport.NextCursor"/> of the previous call; null starts at the beginning.</param>
public sealed record GetContentValidation(Guid SpaceId, Guid? Cursor = null);

public sealed record ContentValidationIssue(Guid PageId, string Title, string Revision, int Version, string Path, string Reason);

/// <param name="Editor">The editor schema the documents were checked against.</param>
/// <param name="Checked">Revisions checked.</param>
/// <param name="Truncated">The call stopped early (more invalid revisions than <see cref="ContentValidationStore.MaximumIssues"/>, or more than <see cref="ContentValidationStore.MaximumPages"/> pages); continue with <paramref name="NextCursor"/>.</param>
/// <param name="NextCursor">Last page fully checked; pass it back as the cursor. Null when the space is done.</param>
public sealed record ContentValidationReport(string Editor, int Checked, IReadOnlyList<ContentValidationIssue> Items, bool Truncated, Guid? NextCursor = null);

public sealed class GetContentValidationHandler(IContentAccess access, ContentValidationStore store)
{
    public async Task<ContentValidationReport> Handle(GetContentValidation query, CancellationToken ct)
    {
        var actor = await access.LockActorAsync(query.SpaceId, ct);
        await access.RequireSpaceAsync(query.SpaceId, SpaceRole.Admin, ct);
        return await store.ValidateAsync(actor, query.SpaceId, query.Cursor, ct);
    }
}

/// <summary>Reads the space in pages of <see cref="BatchSize"/> rows (keyset by page id), so memory stays bounded by one batch.</summary>
public sealed class ContentValidationStore(ISession session)
{
    public const int MaximumIssues = 200;
    /// <summary>Pages scanned per call, so one request cannot hold the actor and content locks while reading a whole large space.</summary>
    public const int MaximumPages = 500;
    private const int BatchSize = 50;

    /// <summary>The editor release whose schema the documents were checked against: Atlas.Markdown.Sync embeds the manifest of
    /// the @atlas/markdown release of the same version.</summary>
    private static readonly string Editor = "@atlas/markdown " + (typeof(MarkdownDocumentValidator).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?");

    public async Task<ContentValidationReport> ValidateAsync(UserAccount actor, Guid spaceId, Guid? cursor, CancellationToken ct)
    {
        var issues = new List<ContentValidationIssue>();
        var checkedRevisions = 0;
        var after = cursor ?? Guid.Empty;
        var scanned = 0;
        while (true)
        {
            // Only pages the actor may read are listed, as in every other space report; other users' never-published drafts stay hidden even from a space administrator (PageVisibility).
            var rows = await session.CreateSQLQuery($$"""
                WITH {{AccessSql.ActorCte}}
                SELECT p."Id" AS id, w."Title" AS title, w."Version" AS version, w."ContentJson" AS working,
                       published."Version" AS published_version,
                       CASE WHEN published."Id" <> w."Id" THEN published."ContentJson" END AS published
                FROM "Page" p
                JOIN "PageRevision" w ON w."PageId"=p."Id" AND w."ActiveTo" IS NULL
                LEFT JOIN "PageRevision" published ON published."Id"=p."PublishedRevisionId"
                CROSS JOIN actor a
                WHERE p."SpaceId"=:space AND NOT p."IsTrashed" AND p."Id">:after
                  AND (p."PublishedRevisionId" IS NOT NULL OR p."OwnerId"=a."Id" OR a."IsWikiAdmin")
                  AND (a."IsWikiAdmin" OR EXISTS (SELECT 1 FROM "EffectivePageAccess" e WHERE e."UserId"=a."Id" AND e."PageId"=p."Id" AND e."Role">=1))
                ORDER BY p."Id" LIMIT :batch
                """)
                .AddScalar("id", NHibernateUtil.Guid).AddScalar("title", NHibernateUtil.String).AddScalar("version", NHibernateUtil.Int32)
                .AddScalar("working", NHibernateUtil.StringClob).AddScalar("published_version", NHibernateUtil.Int32)
                .AddScalar("published", NHibernateUtil.StringClob)
                .SetParameter("actor", actor.Id).SetParameter("stamp", actor.SecurityStamp).SetParameter("space", spaceId)
                .SetParameter("after", after).SetParameter("batch", BatchSize)
                .ListAsync<object[]>(ct);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var (pageId, title) = ((Guid)row[0], (string)row[1]);
                // A title stored before names were one line (Rules.Required): it still saves unchanged, so it is listed for a rename.
                if (title.Any(c => char.IsControl(c) || Rules.IsBidiControl(c)))
                {
                    if (issues.Count == MaximumIssues)
                    {
                        return new ContentValidationReport(Editor, checkedRevisions, issues, true, after == Guid.Empty ? null : after);
                    }
                    issues.Add(new ContentValidationIssue(pageId, title, "working", (int)row[2], "title",
                        "obsahuje zalomení řádku nebo řídicí znaky; stránku přejmenujte"));
                }
                foreach (var (revision, version, json) in new[] { ("working", (int?)row[2], (string?)row[3]), ("published", (int?)row[4], (string?)row[5]) })
                {
                    if (json is null)
                    {
                        continue;
                    }
                    checkedRevisions++;
                    // The editor opens the Atlas 2.x shapes it repairs (LegacyDocumentRepair), so only what it cannot repair is listed.
                    if (HostDocumentValidator.Instance.Validate(LegacyDocumentRepair.Repair(json)) is { } violation)
                    {
                        if (issues.Count == MaximumIssues)
                        {
                            return new ContentValidationReport(Editor, checkedRevisions, issues, true, after == Guid.Empty ? null : after);
                        }
                        issues.Add(new ContentValidationIssue(pageId, title, revision, version!.Value, violation.Path, EditorDocumentErrors.Reason(violation)));
                    }
                }
                after = pageId;
            }
            if (rows.Count < BatchSize)
            {
                return new ContentValidationReport(Editor, checkedRevisions, issues, false);
            }
            scanned += rows.Count;
            if (scanned >= MaximumPages)
            {
                return new ContentValidationReport(Editor, checkedRevisions, issues, true, after);
            }
        }
    }
}
