namespace Atlas.Server.Tests.Platform;

/// <summary>
/// What a seed of bare SQL rows of pages, revisions, boards and spaces writes beside them since migration 2026110930, as the triggers
/// did before it and as the application does for its entities (ContentRowEffects): the envelopes, the tree tokens, the current
/// organization and metadata revisions, the contributions and the version groups. Idempotent: it fills only what is missing.
/// </summary>
internal static class DerivedRows
{
    /// <summary>Over every row of the database: only for a host or database of the test's own.</summary>
    public static readonly string Sql = Text(_ => "true");

    /// <summary>Only for the rows of these spaces (their pages, revisions and boards): on a shared host other tests' rows stay untouched.</summary>
    public static string ForSpaces(params Guid[] spaceIds)
    {
        var ids = string.Join(",", spaceIds.Select(id => $"'{id}'"));
        return Text(column => $"{column} IN ({ids})");
    }

    // within: the filter on a space id column.
    private static string Text(Func<string, string> within) => $"""
        INSERT INTO "SpaceOrganization"("SpaceId") SELECT s."Id" FROM "Space" s
          WHERE {within("s.\"Id\"")} AND NOT EXISTS (SELECT 1 FROM "SpaceOrganization" o WHERE o."SpaceId" = s."Id");
        INSERT INTO "SpaceMetadataRevision"("Id","SpaceId","Name","Icon","Color","Description","PinPolicy","CommentsEnabled","PublicationMode","ActiveFrom")
          SELECT gen_random_uuid(), s."Id", s."Name", s."Icon", s."Color", s."Description", s."PinPolicy", s."CommentsEnabled", s."PublicationMode", clock_timestamp()
          FROM "Space" s WHERE {within("s.\"Id\"")} AND NOT EXISTS (SELECT 1 FROM "SpaceMetadataRevision" r WHERE r."SpaceId" = s."Id" AND r."ActiveTo" IS NULL);
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt")
          SELECT p."Id", 'atlas.page', 'space', p."SpaceId", p."Version", now(), p."OwnerId", now(), p."TrashedAt" FROM "Page" p
          WHERE {within("p.\"SpaceId\"")} AND NOT EXISTS (SELECT 1 FROM "ContentResource" r WHERE r."Id" = p."Id");
        INSERT INTO "ContentResource"("Id","TypeId","Scope","SpaceId","Version","CreatedAt","CreatedBy","UpdatedAt","TrashedAt","Title")
          SELECT b."Id", 'atlas.board', 'space', b."SpaceId", b."Sequence", now(), b."CreatedBy", now(), b."TrashedAt", b."Title" FROM "Board" b
          WHERE {within("b.\"SpaceId\"")} AND NOT EXISTS (SELECT 1 FROM "ContentResource" r WHERE r."Id" = b."Id");
        INSERT INTO "PageOrganizationRevision"("Id","PageId","SpaceId","ParentId","SortOrder","IsTrashed","TrashedAt","TrashedById","TrashBatchId","ActiveFrom")
          SELECT gen_random_uuid(), p."Id", p."SpaceId", p."ParentId", p."SortOrder", p."IsTrashed", p."TrashedAt", p."TrashedById", p."TrashBatchId", clock_timestamp()
          FROM "Page" p WHERE {within("p.\"SpaceId\"")} AND NOT EXISTS (SELECT 1 FROM "PageOrganizationRevision" r WHERE r."PageId" = p."Id" AND r."ActiveTo" IS NULL);
        INSERT INTO "PageContribution"("UserId","PageId","LastRevisionVersion")
          SELECT "AuthorId", "PageId", max("Version") FROM "PageRevision"
          WHERE "PageId" IN (SELECT p."Id" FROM "Page" p WHERE {within("p.\"SpaceId\"")}) GROUP BY "AuthorId", "PageId"
          ON CONFLICT ("UserId","PageId") DO UPDATE SET "LastRevisionVersion" = greatest("PageContribution"."LastRevisionVersion", EXCLUDED."LastRevisionVersion");
        UPDATE "PageRevision" SET "GroupId" = "Id", "GroupStartedAt" = "ActiveFrom"
          WHERE "GroupId" IS NULL AND "PageId" IN (SELECT p."Id" FROM "Page" p WHERE {within("p.\"SpaceId\"")});
        """;
}
