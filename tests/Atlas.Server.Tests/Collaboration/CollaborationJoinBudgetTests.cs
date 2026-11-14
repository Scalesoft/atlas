using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Atlas.Server.Tests.Platform;
using Xunit;
using Xunit.Abstractions;
using static Atlas.Server.Tests.Collaboration.CollaborationContentGuardHttpTests;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>
/// The SQL statements of joining a live page that is already seeded: one authorization serves the join (the page decision of
/// AtlasCollaborationAuthenticator), then the document is loaded. A second or third authorization within the join is the
/// regression this pins.
/// </summary>
public sealed class CollaborationJoinBudgetTests(ITestOutputHelper output)
{
    private const int Budget = 8;

    [PostgreSqlFact]
    public async Task Joining_a_seeded_page_authorizes_once_and_stays_within_its_sql_budget()
    {
        var joins = new JoinStatements();
        await using var site = await AtlasHttpSite.CreateAsync("atlas_collab_join_budget_",
            configureServices: services => CollaborationContentGuardHttpTests.BrowserSeeds(services.Configure<HubOptions>(options => options.AddFilter(joins))));
        var cookies = new CookieContainer();
        using var admin = site.CreateDefaultClient(BaseAddress, new CookieContainerHandler(cookies));
        await site.CompleteSetupAsync(admin);
        var spaces = await AtlasHttpSite.Json(await admin.GetAsync("/api/spaces"), HttpStatusCode.OK);
        var page = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, "/api/pages",
            new { spaceId = spaces[0]!["id"]!.GetValue<string>(), parentId = (string?)null, title = "Join", contentJson = Rich, markdown = "" }), HttpStatusCode.OK);
        var session = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(admin, HttpMethod.Post, $"/api/pages/{page["id"]!.GetValue<string>()}/collaboration"), HttpStatusCode.OK);
        var documentId = session["documentId"]!.GetValue<string>();
        await using var writer = Connect(site, cookies);
        await using var second = Connect(site, cookies);
        await writer.StartAsync();
        await second.StartAsync();
        Assert.True((await JoinAsync(site, writer, documentId)).IsSnapshotter);
        await writer.InvokeAsync("PushSnapshot", documentId, 0L, Baseline, Projections(0, Baseline));

        await JoinAsync(site, second, documentId);

        var statements = joins.Recorded.Last();
        output.WriteLine($"join of a seeded page: {statements.Count} SQL statements (budget {Budget})" + Environment.NewLine + string.Join(Environment.NewLine, statements));
        // Each authorization checks the browser session once.
        Assert.Equal(1, statements.Count(sql => sql.Contains("\"RevokedBrowserSession\"", StringComparison.Ordinal)));
        Assert.True(statements.Count <= Budget, $"join sends {statements.Count} statements, budget {Budget}");
    }

    /// <summary>The statements of each JoinDocument invocation, recorded in the hub's own async flow.</summary>
    private sealed class JoinStatements : IHubFilter
    {
        public ConcurrentQueue<IReadOnlyList<string>> Recorded { get; } = new();

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            if (context.HubMethodName != "JoinDocument")
            {
                return await next(context);
            }
            using var recording = SqlRecording.Start();
            try
            {
                return await next(context);
            }
            finally
            {
                Recorded.Enqueue(recording.Statements);
            }
        }
    }
}
