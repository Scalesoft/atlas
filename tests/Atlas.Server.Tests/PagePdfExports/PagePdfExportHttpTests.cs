using System.Net;
using Atlas.Server.Features.PagePdfExports;
using Atlas.Server.Features.StorageProfiles;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PagePdfExports;

public sealed class PagePdfExportHttpTests
{
    /// <summary>
    /// The handbook scenario: two pages pick one folder of a profile (browsed and created from the export settings) under their own
    /// names, a third the profile root; a name taken by another page, a path outside the root and an existing file the export did
    /// not write are refused until the takeover is confirmed. The storage overview lists the exports and can repeat one.
    /// </summary>
    [PostgreSqlFact]
    public async Task Handbooks_choose_folders_within_the_profile_and_take_over_files_only_when_confirmed()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-folders", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Existující"));
        try
        {
            await using var site = await AtlasHttpSite.CreateAsync("atlas_pdf_folders_");
            using var admin = site.Client();
            await site.CompleteSetupAsync(admin);
            var spaceId = await AtlasHttpSite.OwnSpaceAsync(admin);
            var profile = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/storage-profiles", new
            {
                name = TestLogin.For("Příručky"), destination = root, username = "", domain = "", enabled = true, allowPageExports = true, allowBackups = false,
                allSpaces = true, spaceIds = Array.Empty<Guid>(),
            }), HttpStatusCode.OK);
            var profileId = profile["id"]!.GetValue<string>();
            async Task<string> PageAsync(string title) => (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
                new { spaceId, parentId = (string?)null, title, contentJson = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Příručka"}]}]}}""", markdown = "Příručka" }),
                HttpStatusCode.OK))["id"]!.GetValue<string>();
            var manual = await PageAsync("MPI – Příručka správce");
            var integration = await PageAsync("MPI – Napojení externích systémů");
            var overview = await PageAsync("Přehled");

            var view = await AtlasHttpSite.Json(await admin.GetAsync($"/api/pages/{manual}/pdf-export"), HttpStatusCode.OK);
            Assert.Equal("", view["settings"]!["folder"]!.GetValue<string>());
            // Every test's profiles for all spaces are offered on the shared site: the test's own by its id.
            Assert.Equal(root, view["profiles"]!.AsArray().Single(x => x!["id"]!.GetValue<string>() == profileId)!["location"]!.GetValue<string>());
            var folders = $"/api/pages/{manual}/pdf-export/folders?profileId={profileId}";
            Assert.Equal(["Existující"], Names(await AtlasHttpSite.Json(await admin.GetAsync(folders), HttpStatusCode.OK)));
            var created = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{manual}/pdf-export/folders",
                new { storageProfileId = profileId, parent = "", name = "Modelsoft Identity - MPI" }), HttpStatusCode.OK);
            Assert.Equal("Modelsoft Identity - MPI", created["folder"]!.GetValue<string>());
            Assert.True(Directory.Exists(Path.Combine(root, "Modelsoft Identity - MPI")));
            Assert.Equal(["Existující", "Modelsoft Identity - MPI"], Names(await AtlasHttpSite.Json(await admin.GetAsync(folders), HttpStatusCode.OK)));
            Assert.Equal(HttpStatusCode.Conflict, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{manual}/pdf-export/folders",
                new { storageProfileId = profileId, parent = "", name = "modelsoft identity - mpi" })).StatusCode);
            var nested = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{manual}/pdf-export/folders",
                new { storageProfileId = profileId, parent = "Existující", name = "Archiv" }), HttpStatusCode.OK);
            Assert.Equal("Existující/Archiv", nested["folder"]!.GetValue<string>());
            Assert.Equal(["Archiv"], Names(await AtlasHttpSite.Json(await admin.GetAsync(folders + "&folder=" + Uri.EscapeDataString("Existující")), HttpStatusCode.OK)));
            // Nothing outside the root: no parent segments, no rooted paths, no folder names with separators.
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(folders + "&folder=..")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync(folders + "&folder=" + Uri.EscapeDataString("Existující/../.."))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{manual}/pdf-export/folders",
                new { storageProfileId = profileId, parent = "", name = "../venku" })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(folders + "&folder=Chybí")).StatusCode);
            Assert.False(Directory.Exists(Path.Combine(root, "Chybí")));

            const string folder = "Modelsoft Identity - MPI";
            await File.WriteAllTextAsync(Path.Combine(root, folder, "MPI – Příručka správce.pdf"), "starší ruční PDF");
            await File.WriteAllTextAsync(Path.Combine(root, folder, "MPI – Příručka správce.docx"), "Word");
            object Save(string name, int version = 0, bool confirm = false)
                => new { enabled = true, storageProfileId = profileId, folder, fileName = name, keepVersions = false, publishedOnly = false, expectedVersion = version, confirmTakeover = confirm };
            var refused = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{manual}/pdf-export", Save("MPI – Příručka správce.pdf")), HttpStatusCode.Conflict);
            Assert.Equal(PagePdfExportModule.TakeoverCode, refused["code"]!.GetValue<string>());
            var saved = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{manual}/pdf-export", Save("MPI – Příručka správce.pdf", confirm: true)), HttpStatusCode.OK);
            // The saved target is the export's own from now on: saving it again asks nothing.
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{manual}/pdf-export",
                Save("MPI – Příručka správce.pdf", saved["settings"]!["version"]!.GetValue<int>())), HttpStatusCode.OK);
            var clash = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{integration}/pdf-export", Save("mpi – PŘÍRUČKA SPRÁVCE.pdf")), HttpStatusCode.Conflict);
            Assert.Contains("jiné stránky", clash["title"]!.GetValue<string>());
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{integration}/pdf-export", Save("MPI – Napojení externích systémů.pdf")), HttpStatusCode.OK);
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{overview}/pdf-export",
                new { enabled = true, storageProfileId = profileId, folder = "", fileName = "Přehled.pdf", keepVersions = false, publishedOnly = true, expectedVersion = 0 }), HttpStatusCode.OK);
            Assert.Equal("Word", await File.ReadAllTextAsync(Path.Combine(root, folder, "MPI – Příručka správce.docx")));

            // The overview lists the exports of every test on the shared site: the rows of the test's own pages.
            string[] own = [manual, integration, overview];
            var rows = (await AtlasHttpSite.Json(await admin.GetAsync("/api/storage-profiles/pdf-exports"), HttpStatusCode.OK)).AsArray()
                .Where(row => own.Contains(row!["pageId"]!.GetValue<string>())).ToArray();
            string[] paths = [Path.Combine(root, folder, "MPI – Napojení externích systémů.pdf"), Path.Combine(root, folder, "MPI – Příručka správce.pdf"), Path.Combine(root, "Přehled.pdf")];
            Assert.Equal(paths.Order(StringComparer.Ordinal), rows.Select(row => row!["path"]!.GetValue<string>()).Order(StringComparer.Ordinal));
            Assert.All(rows, row => Assert.Equal("pending", row!["status"]!.GetValue<string>()));
            Assert.True(rows.Single(row => row!["pageId"]!.GetValue<string>() == overview)!["publishedOnly"]!.GetValue<bool>());
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/storage-profiles/pdf-exports/{manual}/run")).StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    /// <summary>The links of an automatic PDF lead to the configured public origin, never to the Host header of whoever saved the rule
    /// (SEC-I-3); without one the rule keeps none and the PDF's Atlas links stay relative.</summary>
    [PostgreSqlTheory]
    [InlineData("https://wiki.example", "https://wiki.example/")]
    [InlineData(null, "")]
    public async Task The_rule_keeps_the_configured_public_origin_whatever_Host_the_request_names(string? configured, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-pdf-origin", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var site = await AtlasHttpSite.CreateAsync("atlas_pdf_origin_", settings =>
            {
                if (configured is null) return;
                settings["Authentication:OpenIdConnect:PublicOrigin"] = configured;
                settings["Authentication:OpenIdConnect:Providers:0:Id"] = "company";
                settings["Authentication:OpenIdConnect:Providers:0:DisplayName"] = "Company";
                settings["Authentication:OpenIdConnect:Providers:0:Issuer"] = "https://idp.example.invalid/";
                settings["Authentication:OpenIdConnect:Providers:0:ClientId"] = "atlas";
            });
            using var admin = site.Client();
            await site.CompleteSetupAsync(admin);
            var spaceId = (await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK))[0]!["id"]!.GetValue<string>();
            var profileId = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/storage-profiles", new
            {
                name = "Příručky", destination = root, username = "", domain = "", enabled = true, allowPageExports = true, allowBackups = false,
                allSpaces = true, spaceIds = Array.Empty<Guid>(),
            }), HttpStatusCode.OK))["id"]!.GetValue<string>();
            var page = (await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
                new { spaceId, parentId = (string?)null, title = "Příručka", contentJson = """{"format":"atlas-rich","version":1,"document":{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"Příručka"}]}]}}""", markdown = "Příručka" }),
                HttpStatusCode.OK))["id"]!.GetValue<string>();

            admin.DefaultRequestHeaders.Host = "evil.example";
            await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Put, $"/api/pages/{page}/pdf-export",
                new { enabled = true, storageProfileId = profileId, folder = "", fileName = "Příručka.pdf", keepVersions = false, publishedOnly = false, expectedVersion = 0 }),
                HttpStatusCode.OK);

            Assert.Equal(expected, await site.ScalarAsync("""SELECT "SourceOrigin" FROM "PagePdfExport" WHERE "PageId"=:page""", ("page", Guid.Parse(page))));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Smb_paths_are_shown_as_unc_paths()
    {
        Assert.Equal(@"\\files\share\Modelsoft Identity - MPI\MPI – Příručka správce.pdf",
            StorageProfilePaths.Display("smb://files/share", "Modelsoft Identity - MPI", "MPI – Příručka správce.pdf"));
        Assert.Equal(@"\\files\share\docs", StorageProfilePaths.Display("smb://files/share/docs", ""));
    }

    private static string[] Names(System.Text.Json.Nodes.JsonNode listing) => listing["folders"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
}
