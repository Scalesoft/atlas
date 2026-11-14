using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests;

/// <summary>
/// Files of the web root and the SPA shell answer without reading the user, so a request that carries the session cookie must not
/// pay the revocation lookup of the cookie handler (AuthCookieModule). A fingerprinted asset, of which a page load asks for dozens,
/// is answered before authentication (HttpHostingTests); the shell and an unversioned file are answered after it, and for them the
/// cookie handler skips the lookup. Server-Timing counts /api requests only, so the statements are recorded with SqlRecording, which
/// sees the host side of an in-process request once the test server preserves the execution context. API requests keep the lookup
/// (PerformanceBudgetHttpTests).
/// </summary>
public sealed class StaticFilesHttpTests
{
    [PostgreSqlFact]
    public async Task Assets_and_the_shell_answer_a_session_cookie_without_the_revocation_lookup()
    {
        var root = Path.Combine(Path.GetTempPath(), "atlas-static-cookie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "executable shell");
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "app-12345678.js"), "executable asset");
        await File.WriteAllTextAsync(Path.Combine(root, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        using var files = new PhysicalFileProvider(root);
        try
        {
            await using var site = await AtlasHttpSite.CreateAsync("atlas_static_cookie_", configureServices: services => services.Replace(ServiceDescriptor.Singleton(files)));
            site.Server.PreserveExecutionContext = true;
            using var signedIn = site.Client();
            var setup = await AtlasHttpSite.SendAsync(signedIn, HttpMethod.Post, "/api/setup/complete",
                new { bootstrapToken = site.BootstrapToken, workspaceName = "Static", username = "administrator", displayName = "Administrator", password = site.Password });
            Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
            var cookie = Assert.Single(setup.Headers.GetValues("Set-Cookie"), x => x.StartsWith("ScaleAtlas.Session=", StringComparison.Ordinal)).Split(';')[0];
            using var copied = site.Client();
            copied.DefaultRequestHeaders.Add("Cookie", cookie);
            using var anonymous = site.Client();
            // The asset leaves before authentication; the unversioned file and the shell are the two answers the cookie handler decides.
            string[] answeredByStaticFiles = ["/assets/app-12345678.js", "/favicon.svg", "/spaces/example"];

            foreach (var path in answeredByStaticFiles)
            {
                var (response, statements) = await GetAsync(signedIn, path);
                Assert.Empty(statements);
                Assert.Equal((await GetAsync(anonymous, path)).Response, response);
            }
            // The same recording sees the lookup where it belongs: the API still validates the session on every request. The probe is
            // /api/auth/me because the cookie handler is its only revocation gate. A content route would answer 401 for a revoked session
            // even if the skip were too wide, since setting the actor checks RevokedBrowserSession itself (AtlasLocks.SetActorAsync).
            Assert.Contains((await GetAsync(signedIn, "/api/auth/me")).Statements, sql => sql.Contains("RevokedBrowserSession"));
            // A file that does not exist is no static answer, so its request keeps the lookup.
            const string missing = "/assets/missing-12345678.js";
            Assert.Contains((await GetAsync(signedIn, missing)).Statements, sql => sql.Contains("RevokedBrowserSession"));

            // A revoked session is refused by the API and gets the same files as everybody else, a missing asset the same 404.
            // The copied cookie is first shown to be a live session, or the comparisons below could compare anonymous with anonymous.
            Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(signedIn, HttpMethod.Post, "/api/auth/logout")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/api/auth/me")).StatusCode);
            foreach (var path in answeredByStaticFiles.Append(missing))
            {
                Assert.Equal((await GetAsync(anonymous, path)).Response, (await GetAsync(copied, path)).Response);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>The status line, headers (except the clock) and body of the answer, and the SQL statements the host sent for it.</summary>
    private static async Task<(string Response, IReadOnlyList<string> Statements)> GetAsync(HttpClient client, string path)
    {
        using var recording = SqlRecording.Start();
        using var response = await client.GetAsync(path);
        var headers = response.Headers.Concat(response.Content.Headers).Where(header => header.Key != "Date")
            .OrderBy(header => header.Key, StringComparer.Ordinal).Select(header => $"{header.Key}: {string.Join(", ", header.Value)}");
        return ($"{(int)response.StatusCode}\n{string.Join('\n', headers)}\n\n{await response.Content.ReadAsStringAsync()}", recording.Statements);
    }
}
