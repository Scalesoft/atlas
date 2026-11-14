using System.IO.Compression;
using System.Text.Json;
using Xunit;
using static Atlas.Server.Tests.PageAccess.ItemAccessSeed;

namespace Atlas.Server.Tests.Backups;

/// <summary>WP3 S5: backups never silently widen access. An archive without own-access pages stays format 1 (readable by 1.0);
/// with one it is format 2 and page.json carries ownAccess. Item grants are not archived (like space grants and accounts), so a
/// restored own-access page is restricted to the space Admin and WikiAdmin until access is granted again.</summary>
public sealed class BackupItemAccessTests
{
    [PostgreSqlFact]
    public async Task Own_access_survives_backup_restore_restricted_and_without_grants()
    {
        await using var source = await BackupFixture.CreateAsync();
        using (var plain = await source.Archive.CreateAsync("full", default))
        {
            Assert.Equal((1, (bool?)null), ManifestAndOwnAccess(plain.Path, source.PageId));
        }
        var reader = await source.Host.SeedUserAsync("pagereader");
        await source.Host.ExecuteAsync("UPDATE \"Page\" SET \"OwnAccess\"=true WHERE \"Id\"=:id", source.PageId);
        await using (var connection = await OpenAsync(source.Host))
        {
            await GrantAsync(connection, source.PageId, reader.Id, null, 1);
        }

        using var archive = await source.Archive.CreateAsync("full", default);
        Assert.Equal((2, (bool?)true), ManifestAndOwnAccess(archive.Path, source.PageId));
        Assert.Null((ManifestAndOwnAccess(archive.Path, source.TrashedPageId)).OwnAccess);

        await using var target = await BackupFixture.CreateAsync();
        await using (var input = File.OpenRead(archive.Path))
        {
            await target.Import.ImportAsync(input, "restore", target.AdminId, default);
        }
        using (var session = target.Host.OpenSession())
        {
            Assert.True((await session.GetAsync<Page>(source.PageId))!.OwnAccess);
            Assert.False((await session.GetAsync<Page>(source.TrashedPageId))!.OwnAccess);
            Assert.Equal(0L, await session.CreateSQLQuery("SELECT count(*) FROM \"ResourceGrant\"").UniqueResultAsync<long>());
        }

        // Alongside (new identities): the copy of the restricted page is restricted too.
        await using (var input = File.OpenRead(archive.Path))
        {
            await source.Import.ImportAsync(input, "add", source.AdminId, default);
        }
        using (var session = source.Host.OpenSession())
        {
            var copies = await session.QueryOver<Page>().Where(x => x.Id != source.PageId && x.Id != source.TrashedPageId).ListAsync();
            Assert.Equal(2, copies.Count);
            Assert.Single(copies, x => x.OwnAccess && !x.IsTrashed);
            Assert.Equal(1L, await session.CreateSQLQuery("SELECT count(*) FROM \"ResourceGrant\" WHERE \"ActiveTo\" IS NULL").UniqueResultAsync<long>());
        }
    }

    private static (int Version, bool? OwnAccess) ManifestAndOwnAccess(string path, Guid pageId)
    {
        using var zip = ZipFile.OpenRead(path);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        using var page = JsonDocument.Parse(zip.GetEntry($"pages/{pageId}/page.json")!.Open());
        return (manifest.RootElement.GetProperty("version").GetInt32(),
            page.RootElement.TryGetProperty("ownAccess", out var own) ? own.GetBoolean() : null);
    }
}
