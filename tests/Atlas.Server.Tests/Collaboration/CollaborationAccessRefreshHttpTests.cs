using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Atlas.Markdown.Sync;
using Atlas.Server.Features.Administration.Contracts;
using Atlas.Server.Tests.Platform;
using Xunit;
using static Atlas.Server.Tests.Collaboration.CollaborationContentGuardHttpTests;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// Since Atlas.Markdown.Sync 0.3.1 collaboration traffic uses each peer's last authorized access. Atlas refreshes live
/// authorization after committing an access change (AccessChanges), so a revoked peer is removed before the command's
/// response returns. The periodic refresh (5 s by default) is pushed out to an hour here: only Atlas's own refresh can revoke
/// the peer, so the revocation arriving at all proves the post-commit refresh.
/// </summary>
public sealed class CollaborationAccessRefreshHttpTests
{
    // The refresh is normally awaited before the response and only the SignalR delivery remains after it; under heavy load
    // the response stops waiting after AccessChanges' 5 s budget, so this bound stays below the default 5 s periodic refresh.
    private static readonly TimeSpan AfterResponse = TimeSpan.FromSeconds(5);

    [PostgreSqlFact]
    public async Task Logout_removes_the_retained_collaboration_session_before_later_updates()
    {
        await using var live = await LiveSession.CreateAsync("atlas_collab_logout_");
        await live.AssertRevokedPromptlyAsync(async () => Assert.Equal(HttpStatusCode.NoContent,
            (await AtlasHttpSite.SendAsync(live.Writer, HttpMethod.Post, "/api/auth/logout")).StatusCode), sessionEnded: true);
    }

    [PostgreSqlFact]
    public async Task Hub_expiry_without_access_change_commit_removes_search_and_live_collaboration()
    {
        await using var live = await LiveSession.CreateAsync("atlas_collab_hub_expiry_", hubMembership: true);
        await live.AssertHubExpiryAsync();
    }

    [PostgreSqlFact]
    public async Task Revoking_a_space_grant_removes_the_live_writer_before_later_updates()
    {
        await using var live = await LiveSession.CreateAsync("atlas_collab_revoke_");
        var path = $"/api/spaces/{live.SpaceId}/access";
        var grant = Assert.Single((await live.Admin.GetFromJsonAsync<SpaceAccessSummary>(path))!.Grants, x => x.UserId == live.WriterId);

        await live.AssertRevokedPromptlyAsync(async () => Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(live.Admin,
            HttpMethod.Put, path, new { userId = live.WriterId, role = (string?)null, expectedVersion = grant.Version })).StatusCode));
    }

    [PostgreSqlFact]
    public async Task Deactivating_an_account_removes_its_live_collaboration_before_later_updates()
    {
        await using var live = await LiveSession.CreateAsync("atlas_collab_deactivate_");
        var user = Assert.Single((await live.Admin.GetFromJsonAsync<List<UserSummary>>("/api/admin/users"))!, x => x.Id == live.WriterId);

        await live.AssertRevokedPromptlyAsync(async () => Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(live.Admin,
            HttpMethod.Put, $"/api/admin/users/{live.WriterId}",
            new { displayName = user.DisplayName, isActive = false, isWikiAdmin = false, expectedVersion = user.Version })).StatusCode));
    }

    [PostgreSqlFact]
    public async Task Switching_a_live_page_to_manual_removes_its_live_reader_before_later_updates()
    {
        await using var live = await LiveSession.CreateAsync("atlas_collab_live_manual_", liveReader: true);
        await live.AssertRevokedPromptlyAsync(async () => Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(live.Admin,
            HttpMethod.Put, $"/api/pages/{live.PageId}/publication", new { mode = "Manual" })).StatusCode));
    }

    [PostgreSqlFact]
    public async Task A_live_reader_follows_the_writers_and_the_hub_refuses_its_updates()
    {
        // The setup proves the reader joined (GET …/collaboration) and received the writer's update.
        await using var live = await LiveSession.CreateAsync("atlas_collab_live_reader_", liveReader: true);
        await live.AssertReaderRefusedAsync();
    }

    /// <summary>A published page edited live by the administrator and by a writer granted through the space; with
    /// <c>liveReader</c> the page is Živě and the second account is a reader watching it.</summary>
    private sealed class LiveSession : IAsyncDisposable
    {
        private readonly List<(bool CanRead, bool CanWrite)> m_accessChanges = [];
        private readonly List<byte[]> m_writerUpdates = [];
        private readonly List<byte[]> m_observerUpdates = [];
        private AtlasHttpSite m_site = null!;
        private HubConnection m_adminHub = null!;
        private HubConnection m_observerHub = null!;
        private HubConnection m_writerHub = null!;
        private string m_documentId = null!;
        private Guid m_pageId;

        public HttpClient Admin { get; private set; } = null!;
        public HttpClient Writer { get; private set; } = null!;
        public Guid SpaceId { get; private set; }
        public Guid WriterId { get; private set; }
        public Guid PageId => m_pageId;

        public static async Task<LiveSession> CreateAsync(string schemaPrefix, bool hubMembership = false, bool liveReader = false)
        {
            var live = new LiveSession();
            live.m_site = await AtlasHttpSite.CreateAsync(schemaPrefix, configureServices: services =>
                CollaborationContentGuardHttpTests.BrowserSeeds(services.Replace(ServiceDescriptor.Singleton(new MarkdownSyncOptions { AccessRefreshInterval = hubMembership ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromHours(1) }))));
            try
            {
                await live.StartAsync(hubMembership, liveReader);
                return live;
            }
            catch
            {
                await live.DisposeAsync();
                throw;
            }
        }

        private async Task StartAsync(bool hubMembership, bool liveReader)
        {
            var adminCookies = new CookieContainer();
            var writerCookies = new CookieContainer();
            Admin = m_site.CreateDefaultClient(BaseAddress, new CookieContainerHandler(adminCookies));
            Writer = m_site.CreateDefaultClient(BaseAddress, new CookieContainerHandler(writerCookies));
            await m_site.CompleteSetupAsync(Admin);
            var spaces = await AtlasHttpSite.Json(await Admin.GetAsync("/api/spaces"), HttpStatusCode.OK);
            SpaceId = Guid.Parse(spaces[0]!["id"]!.GetValue<string>());

            var invitationResponse = await AtlasHttpSite.SendAsync(Admin, HttpMethod.Post, "/api/admin/users",
                new { username = "writer", displayName = "Live writer", isWikiAdmin = false });
            Assert.Equal(HttpStatusCode.Created, invitationResponse.StatusCode);
            var invitation = (await invitationResponse.Content.ReadFromJsonAsync<CreatedBuiltinUser>())!;
            WriterId = invitation.User.Id;
            Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(Writer, HttpMethod.Post, "/api/auth/activate",
                new { token = invitation.Token, newPassword = m_site.Password })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(Writer, HttpMethod.Post, "/api/auth/login",
                new { username = "writer", password = m_site.Password })).StatusCode);
            if (hubMembership)
                await m_site.ExecuteAsync("""
                    INSERT INTO "HubConnection"("Id") VALUES (@connection);
                    INSERT INTO "HubConnectionRevision"("Id","ConnectionId","Issuer","BaseUrl","ProtectedToken","Mode","ChangedById","ActiveFrom")
                    VALUES (gen_random_uuid(),@connection,'https://hub.test','https://hub.test','unused',2,@user,statement_timestamp()-interval '1 hour');
                    INSERT INTO "ExternalIdentity"("Id","UserId","Issuer","Subject","ActiveFrom")
                    VALUES (@identity,@user,'https://hub.test','writer-id',statement_timestamp()-interval '1 hour');
                    INSERT INTO "UserGroup"("Id","Name") VALUES (@group,'Hub writers');
                    INSERT INTO "HubGroupBinding"("GroupId","ConnectionId","ExternalGroupId") VALUES (@group,@connection,'writers-id');
                    INSERT INTO "GroupMember"("Id","GroupId","UserId","ActiveFrom") VALUES (gen_random_uuid(),@group,@user,statement_timestamp());
                    INSERT INTO "SpaceGrant"("Id","SpaceId","GroupId","Role","ActiveFrom") VALUES(gen_random_uuid(),@space,@group,2,statement_timestamp());
                    INSERT INTO "HubIdentityState"("ExternalIdentityId","UserId","ConnectionId","ExternalUserId","SnapshotStartedAt","VerifiedUntil","LastSuccessAt","LastAttemptAt","NextAttemptAt","UpstreamAccountState","Generation")
                    VALUES (@identity,@user,@connection,'writer-id',statement_timestamp(),statement_timestamp()+interval '5 minutes',statement_timestamp(),statement_timestamp(),statement_timestamp()+interval '1 hour',1,1);
                    """, ("connection", Guid.NewGuid()), ("identity", Guid.NewGuid()), ("group", Guid.NewGuid()), ("user", WriterId), ("space", SpaceId));
            else
                Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(Admin, HttpMethod.Put, $"/api/spaces/{SpaceId}/access",
                    new { userId = WriterId, role = liveReader ? "Reader" : "Writer", expectedVersion = (string?)null })).StatusCode);

            var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Admin, HttpMethod.Post, "/api/pages",
                new { spaceId = SpaceId, parentId = (string?)null, title = "Live", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
            var pageId = page["id"]!.GetValue<string>();
            m_pageId = Guid.Parse(pageId);
            // A draft is visible only to its owner; the writer joins the published page.
            Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(Admin, HttpMethod.Post, $"/api/pages/{pageId}/publish",
                new { expectedVersion = page["version"]!.GetValue<long>() })).StatusCode);
            if (liveReader)
                Assert.Equal(HttpStatusCode.OK, (await AtlasHttpSite.SendAsync(Admin, HttpMethod.Put, $"/api/pages/{pageId}/publication", new { mode = "Live" })).StatusCode);
            var adminSession = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Admin, HttpMethod.Post, $"/api/pages/{pageId}/collaboration"), HttpStatusCode.OK);
            var writerSession = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(Writer, liveReader ? HttpMethod.Get : HttpMethod.Post,
                $"/api/pages/{pageId}/collaboration"), HttpStatusCode.OK);
            m_documentId = adminSession["documentId"]!.GetValue<string>();
            Assert.Equal(m_documentId, writerSession["documentId"]!.GetValue<string>());

            m_adminHub = Connect(m_site, adminCookies);
            m_observerHub = Connect(m_site, adminCookies);
            m_writerHub = Connect(m_site, writerCookies);
            m_writerHub.On<string, bool, bool>("DocumentAccessChanged", (_, canRead, canWrite) => { lock (m_accessChanges) m_accessChanges.Add((canRead, canWrite)); });
            m_writerHub.On<string, byte[], long>("ReceiveUpdate", (_, update, _) => { lock (m_writerUpdates) m_writerUpdates.Add(update); });
            m_observerHub.On<string, byte[], long>("ReceiveUpdate", (_, update, _) => { lock (m_observerUpdates) m_observerUpdates.Add(update); });
            await m_adminHub.StartAsync();
            await m_observerHub.StartAsync();
            await m_writerHub.StartAsync();
            Assert.True((await JoinAsync(m_site, m_adminHub, m_documentId)).IsSnapshotter);
            await m_adminHub.InvokeAsync("PushSnapshot", m_documentId, 0L, Baseline, Projections(0, Baseline));
            await JoinAsync(m_site, m_observerHub, m_documentId);
            await JoinAsync(m_site, m_writerHub, m_documentId);

            // Both directions work while the writer holds access.
            await m_adminHub.InvokeAsync<long>("SendUpdate", m_documentId, Guid.NewGuid().ToString("D"), HonestEdit);
            await WaitUntilAsync(() => Count(m_writerUpdates) == 1 && Count(m_observerUpdates) == 1);
            if (liveReader) return;
            await m_writerHub.InvokeAsync<long>("SendUpdate", m_documentId, Guid.NewGuid().ToString("D"), HonestEdit);
            await WaitUntilAsync(() => Count(m_observerUpdates) == 2);
        }

        public async Task AssertRevokedPromptlyAsync(Func<Task> revoke, bool sessionEnded = false)
        {
            await revoke();
            var clock = Stopwatch.StartNew();
            await WaitUntilAsync(() => Count(m_accessChanges) > 0);
            Assert.True(clock.Elapsed < AfterResponse, $"revocation arrived {clock.Elapsed} after the response");
            lock (m_accessChanges)
            {
                Assert.Equal((false, false), Assert.Single(m_accessChanges));
            }

            // Content written after the revocation reaches the remaining peer only.
            var writerBefore = Count(m_writerUpdates);
            var observerBefore = Count(m_observerUpdates);
            await m_adminHub.InvokeAsync<long>("SendUpdate", m_documentId, Guid.NewGuid().ToString("D"), HonestEdit);
            await WaitUntilAsync(() => Count(m_observerUpdates) == observerBefore + 1);
            if (sessionEnded)
            {
                // Logout also rejects the cookie on the next long-poll request, closing this transport entirely.
                await WaitUntilAsync(() => m_writerHub.State == HubConnectionState.Disconnected);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    m_writerHub.InvokeAsync<long>("SendUpdate", m_documentId, Guid.NewGuid().ToString("D"), HonestEdit));
            }
            else
            {
                var refused = await Assert.ThrowsAsync<HubException>(() =>
                    m_writerHub.InvokeAsync<long>("SendUpdate", m_documentId, Guid.NewGuid().ToString("D"), HonestEdit));
                Assert.Contains("[FORBIDDEN]", refused.Message, StringComparison.Ordinal);
            }
            // The writer has processed the refusal or disconnected after the prior broadcast.
            Assert.Equal(writerBefore, Count(m_writerUpdates));
        }

        /// <summary>A read-only peer's update is refused and reaches no one; the peer stays joined and keeps receiving.</summary>
        public async Task AssertReaderRefusedAsync()
        {
            var observerBefore = Count(m_observerUpdates);
            var refused = await Assert.ThrowsAsync<HubException>(() =>
                m_writerHub.InvokeAsync<long>("SendUpdate", m_documentId, Guid.NewGuid().ToString("D"), HonestEdit));
            Assert.Contains("[READ_ONLY]", refused.Message, StringComparison.Ordinal);
            var writerBefore = Count(m_writerUpdates);
            await m_adminHub.InvokeAsync<long>("SendUpdate", m_documentId, Guid.NewGuid().ToString("D"), HonestEdit);
            await WaitUntilAsync(() => Count(m_writerUpdates) == writerBefore + 1 && Count(m_observerUpdates) == observerBefore + 1);
        }

        public async Task AssertHubExpiryAsync()
        {
            var before = await AtlasHttpSite.Json(await Writer.GetAsync("/api/search?q=Live"), HttpStatusCode.OK);
            Assert.Contains(m_pageId.ToString(), before.ToJsonString(), StringComparison.Ordinal);
            await AssertRevokedPromptlyAsync(async () =>
            {
                // Change only the stored deadline, without AccessChanges: the periodic authorization must notice it.
                await m_site.ExecuteAsync("""
                    UPDATE "HubIdentityState" SET "SnapshotStartedAt"=statement_timestamp()-interval '6 minutes',
                        "VerifiedUntil"=statement_timestamp()-interval '1 minute' WHERE "UserId"=@user
                    """, ("user", WriterId));
                var after = await AtlasHttpSite.Json(await Writer.GetAsync("/api/search?q=Live"), HttpStatusCode.OK);
                Assert.DoesNotContain(m_pageId.ToString(), after.ToJsonString(), StringComparison.Ordinal);
                Assert.Equal(HttpStatusCode.NotFound, (await Writer.GetAsync($"/api/pages/{m_pageId}")).StatusCode);
            });
        }

        private static int Count<T>(List<T> items)
        {
            lock (items)
            {
                return items.Count;
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var hub in new[] { m_writerHub, m_observerHub, m_adminHub })
            {
                if (hub is not null)
                {
                    await hub.DisposeAsync();
                }
            }
            Writer?.Dispose();
            Admin?.Dispose();
            await m_site.DisposeAsync();
        }
    }
}
