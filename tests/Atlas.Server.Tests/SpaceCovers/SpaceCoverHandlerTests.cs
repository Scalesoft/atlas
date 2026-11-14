using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Features.Attachments;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.SpaceCovers.Commands;
using Atlas.Server.Features.SpaceCovers.Contracts;
using Atlas.Server.Features.SpaceCovers.Domain;
using Atlas.Server.Features.SpaceCovers.Queries;
using Atlas.Server.Features.Spaces.Contracts;
using Atlas.Server.Features.Spaces.Queries;
using Atlas.Server.Features.SpaceSettings.Commands;
using Atlas.Server.Features.SpaceSettings.Contracts;
using Atlas.Server.Features.SpaceSettings.Queries;
using Atlas.Server.Tests.SpaceSettings;
using Xunit;

namespace Atlas.Server.Tests.SpaceCovers;

public sealed class SpaceCoverHandlerTests
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6Cj8AAAAASUVORK5CYII=");
    internal static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0xFF, 0xD9];
    private static readonly byte[] Webp = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8, 0x18, 0, 0, 0];

    [PostgreSqlFact]
    public async Task Upload_accepts_png_jpeg_and_webp_by_content_up_to_5_MB_and_writes_nothing_it_refuses()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var files = f.Host.Services.GetRequiredService<AttachmentFiles>();
        var before = Directory.Exists(files.Root) ? Directory.GetFiles(files.Root).ToHashSet() : [];
        // The declared type and name never reach the handler: an SVG (script), a GIF and a cut-off PNG fail on their bytes.
        byte[][] refusedBytes = ["<svg xmlns='http://www.w3.org/2000/svg' onload='alert(1)'/>"u8.ToArray(), [.. "GIF89a"u8, 1, 0, 1, 0, 0, 0, 0], Png[..12]];
        foreach (var refused in refusedBytes)
        {
            await Denied(400, () => f.As<SpaceCoverChange>(f.SpaceAdmin, new ReplaceSpaceCover(f.SpaceId, refused)));
        }
        var oversized = new byte[SpaceCoverImage.MaximumBytes + 1];
        Webp.CopyTo(oversized, 0);
        await Denied(413, () => f.As<SpaceCoverChange>(f.SpaceAdmin, new ReplaceSpaceCover(f.SpaceId, oversized)));
        // Other tests write (and delete) files in the same root: a leak of this test is a new file holding a refused payload or the
        // oversized one, told apart by its length before its bytes are read.
        Assert.DoesNotContain(Directory.Exists(files.Root) ? Directory.GetFiles(files.Root).Except(before) : [], path =>
            new FileInfo(path) is { Exists: true } file && (file.Length == oversized.Length
                || refusedBytes.Any(bytes => bytes.Length == file.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))));

        var largest = oversized[..(int)SpaceCoverImage.MaximumBytes];
        var change = await f.As<SpaceCoverChange>(f.SpaceAdmin, new ReplaceSpaceCover(f.SpaceId, largest));
        Assert.Equal(new SpaceCoverChange(new SpaceCoverDto("Upload", null, Sha256(largest)[..16], true), null), change);
        var stored = await CurrentAsync(f);
        Assert.Equal(("image/webp", SpaceCoverImage.MaximumBytes), (stored.MimeType, stored.Size!.Value));
        Assert.Equal(largest, await File.ReadAllBytesAsync(files.PathFor(stored.FileId!.Value)));
    }

    [PostgreSqlFact]
    public async Task Replace_keeps_the_tint_releases_the_previous_file_and_every_change_is_a_revision_with_its_actor()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var files = f.Host.Services.GetRequiredService<AttachmentFiles>();
        var first = await f.As<SpaceCoverChange>(f.SpaceAdmin, new ReplaceSpaceCover(f.SpaceId, Png));
        Assert.True(first.Cover.Tinted);
        var firstFile = (await CurrentAsync(f)).FileId!.Value;
        var untinted = await f.As<SpaceCoverChange>(f.SpaceAdmin, new SetSpaceCoverTint(f.SpaceId, false));
        Assert.Equal(new SpaceCoverChange(first.Cover with { Tinted = false }, null), untinted);
        Assert.Equal(untinted, await f.As<SpaceCoverChange>(f.SpaceAdmin, new SetSpaceCoverTint(f.SpaceId, false)));

        // A WikiAdmin may change it too. The previous file is only released: the endpoint deletes it after the commit.
        var second = await f.As<SpaceCoverChange>(f.Admin, new ReplaceSpaceCover(f.SpaceId, Jpeg));
        Assert.Equal(new SpaceCoverChange(new SpaceCoverDto("Upload", null, Sha256(Jpeg)[..16], false), firstFile), second);
        var secondFile = (await CurrentAsync(f)).FileId!.Value;
        Assert.True(File.Exists(files.PathFor(secondFile)));
        Assert.Equal(second.Cover, (await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId))).Cover);

        Assert.Equal(new SpaceCoverChange(SpaceCoverDto.Unmatched, secondFile), await f.As<SpaceCoverChange>(f.SpaceAdmin, new RemoveSpaceCover(f.SpaceId)));
        Assert.Equal(new SpaceCoverChange(SpaceCoverDto.Unmatched, null), await f.As<SpaceCoverChange>(f.SpaceAdmin, new RemoveSpaceCover(f.SpaceId)));
        Assert.Equal(SpaceCoverDto.Unmatched, (await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId))).Cover);
        await Denied(404, () => f.As<SpaceCoverChange>(f.SpaceAdmin, new SetSpaceCoverTint(f.SpaceId, true)));
        // After a removal the next image is a first one again.
        Assert.True((await f.As<SpaceCoverChange>(f.SpaceAdmin, new ReplaceSpaceCover(f.SpaceId, Png))).Cover.Tinted);

        using var session = f.Host.OpenSession();
        var history = await session.QueryOver<SpaceCover>().Where(x => x.SpaceId == f.SpaceId).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        Assert.Equal(new[] { (true, f.SpaceAdmin.Id), (false, f.SpaceAdmin.Id), (false, f.Admin.Id), (false, f.SpaceAdmin.Id), (true, f.SpaceAdmin.Id) },
            history.Select(x => (x.FileId is not null && x.Tinted, x.ChangedBy!.Value)));
        Assert.Null(history[3].FileId);
        Assert.All(history.Zip(history.Skip(1)), pair => Assert.Equal(pair.Second.ActiveFrom, pair.First.ActiveTo));
        Assert.Null(history[^1].ActiveTo);
    }

    [PostgreSqlFact]
    public async Task Only_the_space_admin_or_wiki_admin_changes_the_image_and_whoever_lists_the_space_sees_it()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var writer = await f.Host.SeedUserAsync("writer");
        var pageReader = await f.Host.SeedUserAsync("pagereader");
        var outsider = await f.Host.SeedUserAsync("outsider");
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = writer.Id, Role = (int)SpaceRole.Writer }));
        // pageReader reaches one page with its own access and nothing else of the space (ItemAccessOnly).
        const string rich = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph"}]}}""";
        var page = await f.As<PageDto>(f.SpaceAdmin, new CreatePage(new CreatePageRequest(f.SpaceId, null, "Shared", rich, "")));
        await f.As<PageDto>(f.SpaceAdmin, new PublishPage(page.Id, new PublishPageRequest(page.Version)));
        await f.Execute("UPDATE \"Page\" SET \"OwnAccess\"=true WHERE \"Id\"=:id", page.Id);
        await f.Execute($"INSERT INTO \"ResourceGrant\"(\"Id\",\"PageId\",\"UserId\",\"Role\") VALUES (gen_random_uuid(),:id,'{pageReader.Id}',1)", page.Id);

        // The automatic pick may come from the description, which pageReader does not see: its list must not tell.
        var settings = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId));
        await f.As<SpaceSettingsDto>(f.SpaceAdmin, new SaveSpaceSettings(f.SpaceId,
            new(settings.Name, settings.Icon, settings.Color, "Evidence faktur a plateb.", settings.PinPolicy, settings.Version)));
        Assert.Equal("ops-invoicing", Assert.Single((await f.As<AccessibleSpaces>(f.SpaceAdmin, new GetAccessibleSpaces())).Items).Cover.CatalogKey);
        var unseen = Assert.Single((await f.As<AccessibleSpaces>(pageReader, new GetAccessibleSpaces())).Items);
        Assert.Equal((true, SpaceCoverDto.Unmatched), (unseen.ItemAccessOnly, unseen.Cover));

        await Denied(403, () => f.As<SpaceCoverChange>(writer, new ReplaceSpaceCover(f.SpaceId, Png)));
        var cover = (await f.As<SpaceCoverChange>(f.SpaceAdmin, new ReplaceSpaceCover(f.SpaceId, Png))).Cover;
        await Denied(403, () => f.As<SpaceCoverChange>(writer, new SetSpaceCoverTint(f.SpaceId, false)));
        await Denied(403, () => f.As<SpaceCoverChange>(writer, new RemoveSpaceCover(f.SpaceId)));

        var opened = await f.As<SpaceCoverContent>(writer, new OpenSpaceCover(f.SpaceId));
        Assert.Equal(("image/png", cover.Version, Sha256(Png)), (opened.MimeType, opened.Version, opened.Sha256));
        Assert.Equal(opened, await f.As<SpaceCoverContent>(pageReader, new OpenSpaceCover(f.SpaceId)));
        var listed = Assert.Single((await f.As<AccessibleSpaces>(pageReader, new GetAccessibleSpaces())).Items);
        Assert.Equal((true, cover), (listed.ItemAccessOnly, listed.Cover));
        await Denied(404, () => f.As<SpaceCoverContent>(outsider, new OpenSpaceCover(f.SpaceId)));
        await Denied(404, () => f.As<SpaceCoverContent>(f.Admin, new OpenSpaceCover(Guid.NewGuid())));
    }

    /// <summary>Auto is never stored: the pick is computed from the space on every read, so a rename changes it without a write,
    /// and a chosen drawing the running catalog no longer knows reads as that pick too.</summary>
    [PostgreSqlFact]
    public async Task The_automatic_pick_follows_the_space_on_every_read_without_a_stored_row()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var settings = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId));
        Assert.Equal(SpaceCoverDto.Unmatched, settings.Cover);
        Assert.Equal(SpaceCoverDto.Unmatched, await ListedCoverAsync(f));

        var pick = new SpaceCoverDto("Auto", "it-backup", null, true);
        var renamed = await f.As<SpaceSettingsDto>(f.SpaceAdmin, new SaveSpaceSettings(f.SpaceId,
            new("Zálohy databází", settings.Icon, settings.Color, settings.Description, settings.PinPolicy, settings.Version)));
        Assert.Equal(pick, renamed.Cover);
        Assert.Equal(pick, await ListedCoverAsync(f));
        using (var session = f.Host.OpenSession())
        {
            Assert.Equal(0, await session.QueryOver<SpaceCover>().Where(x => x.SpaceId == f.SpaceId).RowCountAsync());
        }

        // A drawing chosen under an earlier catalog: the database accepts any well-formed key.
        await f.Host.SeedAsync(session => SpaceCoverHistory.ChooseAsync(session, f.SpaceId, SpaceCoverSource.Catalog, "retired-drawing", f.SpaceAdmin.Id, default));
        Assert.Equal(pick, (await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId))).Cover);
        Assert.Equal(pick, await ListedCoverAsync(f));
    }

    [PostgreSqlFact]
    public async Task A_choice_moves_between_the_sources_releases_an_upload_and_writes_a_row_only_when_it_changes()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        // No row is the automatic image already.
        Assert.Equal(new SpaceCoverChange(SpaceCoverDto.Unmatched, null), await f.As<SpaceCoverChange>(f.SpaceAdmin, new ChooseSpaceCover(f.SpaceId, "Auto", null)));
        var chosen = new SpaceCoverChange(new SpaceCoverDto("Catalog", "it-backup", null, true), null);
        Assert.Equal(chosen, await f.As<SpaceCoverChange>(f.SpaceAdmin, new ChooseSpaceCover(f.SpaceId, "Catalog", "it-backup")));
        Assert.Equal(chosen, await f.As<SpaceCoverChange>(f.Admin, new ChooseSpaceCover(f.SpaceId, "Catalog", "it-backup")));
        Assert.Equal(chosen.Cover, (await f.As<SpaceSettingsDto>(f.SpaceAdmin, new GetSpaceSettings(f.SpaceId))).Cover);

        // A choice over an upload releases its file, as a removal does; the tint has nothing left to change.
        await f.As<SpaceCoverChange>(f.SpaceAdmin, new ReplaceSpaceCover(f.SpaceId, Png));
        var uploaded = (await CurrentAsync(f)).FileId!.Value;
        Assert.Equal(new SpaceCoverChange(new SpaceCoverDto("None", null, null, true), uploaded),
            await f.As<SpaceCoverChange>(f.SpaceAdmin, new ChooseSpaceCover(f.SpaceId, "None", null)));
        await Denied(404, () => f.As<SpaceCoverChange>(f.SpaceAdmin, new SetSpaceCoverTint(f.SpaceId, false)));
        Assert.Equal(new SpaceCoverChange(SpaceCoverDto.Unmatched, null), await f.As<SpaceCoverChange>(f.SpaceAdmin, new RemoveSpaceCover(f.SpaceId)));

        // Neither the first Auto nor the wiki admin's repeated choice wrote a row.
        using var session = f.Host.OpenSession();
        var history = await session.QueryOver<SpaceCover>().Where(x => x.SpaceId == f.SpaceId).OrderBy(x => x.ActiveFrom).Asc.ListAsync();
        (SpaceCoverSource, string?)[] expected = [(SpaceCoverSource.Catalog, "it-backup"), (SpaceCoverSource.Upload, null), (SpaceCoverSource.None, null), (SpaceCoverSource.Auto, null)];
        Assert.Equal(expected, history.Select(x => (x.Source, x.CatalogKey)));
        Assert.All(history.Zip(history.Skip(1)), pair => Assert.Equal(pair.Second.ActiveFrom, pair.First.ActiveTo));
        Assert.Null(history[^1].ActiveTo);
    }

    [PostgreSqlFact]
    public async Task A_choice_needs_a_known_drawing_the_right_shape_and_the_space_admin()
    {
        await using var f = await SpaceSettingsFixture.CreateAsync();
        var writer = await f.Host.SeedUserAsync("writer");
        await f.Host.SeedAsync(session => session.SaveAsync(new SpaceGrant { Id = Guid.NewGuid(), SpaceId = f.SpaceId, UserId = writer.Id, Role = (int)SpaceRole.Writer }));
        // An upload has its own endpoint: a choice cannot claim a file.
        ChooseSpaceCover[] refused =
        [
            new(f.SpaceId, "Catalog", "no-such-drawing"), new(f.SpaceId, "Catalog", null), new(f.SpaceId, "None", "it-backup"), new(f.SpaceId, "Upload", null)
        ];
        foreach (var choice in refused)
        {
            await Denied(400, () => f.As<SpaceCoverChange>(f.SpaceAdmin, choice));
        }
        // Whoever may not change the space gets its 403 before any validation answer.
        await Denied(403, () => f.As<SpaceCoverChange>(writer, new ChooseSpaceCover(f.SpaceId, "Catalog", "it-backup")));
        await Denied(403, () => f.As<SpaceCoverChange>(writer, refused[0]));
        using var session = f.Host.OpenSession();
        Assert.Equal(0, await session.QueryOver<SpaceCover>().Where(x => x.SpaceId == f.SpaceId).RowCountAsync());
    }

    private static async Task<SpaceCoverDto> ListedCoverAsync(SpaceSettingsFixture f) =>
        Assert.Single((await f.As<AccessibleSpaces>(f.SpaceAdmin, new GetAccessibleSpaces())).Items, x => x.Id == f.SpaceId).Cover;

    private static async Task<SpaceCover> CurrentAsync(SpaceSettingsFixture f)
    {
        using var session = f.Host.OpenSession();
        return (await SpaceCoverHistory.CurrentAsync(session, f.SpaceId, default))!;
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task Denied(int status, Func<Task> action)
        => Assert.Equal(status, (await Assert.ThrowsAsync<ApiException>(action)).Status);
}
