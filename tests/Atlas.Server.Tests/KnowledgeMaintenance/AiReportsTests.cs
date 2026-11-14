using System.Text.Json;
using NHibernate;
using Atlas.Server.Features.KnowledgeMaintenance;
using Atlas.Server.Tests.AiIndex;
using Xunit;

namespace Atlas.Server.Tests.KnowledgeMaintenance;

public sealed class AiReportsTests
{
    [PostgreSqlFact]
    public async Task Usage_aggregates_the_week_without_turning_unknown_tokens_into_zero()
    {
        await using var f = await IndexFixture.CreateAsync();
        await f.ConfigureAsync();
        var now = f.Clock.GetUtcNow().UtcDateTime;
        await f.Host.SeedAsync(async session =>
        {
            var profile = await session.CreateSQLQuery("""SELECT "Id" FROM "AiProfileRevision" LIMIT 1""")
                .AddScalar("Id", NHibernateUtil.Guid).UniqueResultAsync<Guid>();
            await session.CreateSQLQuery("""
                INSERT INTO "AiUsage" ("OccurredAt","Task","Kind","Operation","ProfileRevisionId","Model","InputTokens","OutputTokens","LatencyMs","Outcome")
                VALUES (:now,'editor.transform','quick','chat',:profile,'test',100,40,100,'ok'),
                       (:now,'editor.transform','quick','chat',:profile,'test',NULL,NULL,200,'timeout'),
                       (:now,'editor.transform','quick','chat',:profile,'test',NULL,NULL,300,'canceled'),
                       (:old,'editor.transform','quick','chat',:profile,'test',999,999,999,'ok'),
                       (:now,'editor.intent','quick','chat',:profile,'test',NULL,NULL,10,'ok')
                """).SetParameter("now", now).SetParameter("old", now.AddDays(-8)).SetParameter("profile", profile).ExecuteUpdateAsync();
        });
        var report = await f.As<AiUsageReport>(f.Admin, new GetAiUsageReport());
        var transform = Assert.Single(report.Items, x => x.Task == "editor.transform");
        Assert.Equal((3L, 1L, 100L, 40L, 1L), (transform.Calls, transform.Errors, transform.InputTokens, transform.OutputTokens, transform.InputKnownCalls));
        Assert.Equal((200d, 290d), (transform.P50Ms, transform.P95Ms));
        var intent = Assert.Single(report.Items, x => x.Task == "editor.intent");
        Assert.Null(intent.InputTokens);
        Assert.Null(intent.EstimatedCost);
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<AiUsageReport>(f.Writer, new GetAiUsageReport()))).Status);
    }


    [PostgreSqlFact]
    public async Task Usage_attributes_historical_providers_and_only_fully_known_costs_to_utc_days()
    {
        await using var f = await IndexFixture.CreateAsync();
        var now = f.Clock.GetUtcNow().UtcDateTime;
        var company = Guid.NewGuid();
        var cloud = Guid.NewGuid();
        await f.Host.SeedAsync(session => session.CreateSQLQuery("""
            INSERT INTO "AiProfileRevision" ("Id","Kind","Enabled","Provider","Location","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt","InputPricePerMillion","OutputPricePerMillion")
            VALUES (:company,'quick',true,'openai-compatible','company','local',30,1,:admin,:now,1,2),
                   (:cloud,'quick',true,'anthropic','cloud','remote',30,1,:admin,:now,3,4);
            INSERT INTO "AiUsage" ("OccurredAt","Task","Kind","Operation","ProfileRevisionId","Model","InputTokens","OutputTokens","LatencyMs","Outcome")
            VALUES (:now,'editor.transform','quick','chat',:company,'local-actual',1000000,500000,50,'ok'),
                   (:now,'editor.transform','quick','chat',:cloud,'remote-actual',1000000,500000,60,'ok'),
                   (:yesterday,'editor.transform','quick','chat',:cloud,'remote-actual',NULL,NULL,70,'timeout'),
                   (:first,'editor.transform','quick','chat',:company,'local-actual',NULL,NULL,70,'canceled'),
                   (:outside,'editor.transform','quick','chat',:cloud,'remote-actual',1000000,500000,80,'ok')
            """).SetParameter("company", company).SetParameter("cloud", cloud).SetParameter("admin", f.Admin.Id)
            .SetParameter("now", now).SetParameter("yesterday", now.AddDays(-1))
            .SetParameter("first", now.Date.AddDays(-6)).SetParameter("outside", now.Date.AddDays(-7).AddHours(23)).ExecuteUpdateAsync());

        var report = await f.As<AiUsageReport>(f.Admin, new GetAiUsageReport());
        Assert.Equal(2, report.Providers.Count);
        Assert.Equal((2L, 7m), (Assert.Single(report.Items).CostKnownCalls, Assert.Single(report.Items).EstimatedCost));
        var local = Assert.Single(report.Providers, x => x.Location == "company");
        Assert.Equal(("openai-compatible", "local-actual", 2L, 1L, 2m),
            (local.Provider, local.Model, local.Calls, local.CostKnownCalls, local.EstimatedCost));
        var remote = Assert.Single(report.Providers, x => x.Location == "cloud");
        Assert.Equal(("anthropic", "remote-actual", 2L, 1L, 1L, 1L, 5m),
            (remote.Provider, remote.Model, remote.Calls, remote.Errors, remote.InputKnownCalls, remote.CostKnownCalls, remote.EstimatedCost));
        Assert.Equal(now, remote.LastCallAt, TimeSpan.FromMilliseconds(1));
        Assert.Equal(7, report.Days.Count);
        Assert.Equal((2L, 2L, 7m), (report.Days[^1].Calls, report.Days[^1].CostKnownCalls, report.Days[^1].EstimatedCost));
        Assert.Equal((1L, 1L, 0L, (decimal?)null), (report.Days[^2].Calls, report.Days[^2].Errors, report.Days[^2].CostKnownCalls, report.Days[^2].EstimatedCost));
        Assert.Equal(now.Date, report.Days[^1].Day);
        Assert.Equal((1L, 1L), (report.Days[0].Calls, report.Days[0].Canceled));
        Assert.Equal(report.Providers.Sum(x => x.Calls), report.Days.Sum(x => x.Calls));
        Assert.Equal(report.Providers.Sum(x => x.EstimatedCost), report.Days.Sum(x => x.EstimatedCost));
    }

    [PostgreSqlFact]
    public async Task Quality_returns_only_aggregate_ratings_and_reasons_even_when_private_notes_exist()
    {
        await using var f = await IndexFixture.CreateAsync();
        var now = f.Clock.GetUtcNow().UtcDateTime;
        await f.Host.SeedAsync(s => s.CreateSQLQuery("""
            INSERT INTO "AiFeedback" ("Id","Task","PromptVersion","Rating","Reason","Note","CreatedAt","UpdatedAt")
            VALUES (gen_random_uuid(),'search.answer','answer@1','down','wrong_sources','PRIVATE-NOTE',:now,:now),
                   (gen_random_uuid(),'search.answer','answer@1','down','wrong_sources','SECOND-PRIVATE-NOTE',:now,:now),
                   (gen_random_uuid(),'search.answer','answer@1','up',NULL,NULL,:now,:now),
                   (gen_random_uuid(),'search.answer','answer@1','up',NULL,NULL,:old,:old)
            """).SetParameter("now", now).SetParameter("old", now.AddDays(-8)).ExecuteUpdateAsync());
        await f.ConfigureAsync();
        await f.Host.SeedAsync(async session =>
        {
            var profile = await session.CreateSQLQuery("""SELECT "Id" FROM "AiProfileRevision" LIMIT 1""")
                .AddScalar("Id", NHibernateUtil.Guid).UniqueResultAsync<Guid>();
            var conversation = Guid.NewGuid();
            await session.CreateSQLQuery("""
                INSERT INTO "AiConversation" ("Id","OwnerId","Origin","Listed","ChatProfileRevisionId","ChatCompatibility","CreatedAt","LastActivityAt","Version")
                VALUES (:id,:owner,'assistant',true,:profile,repeat('a',64),:now,:now,1)
                """).SetParameter("id", conversation).SetParameter("owner", f.Writer.Id).SetParameter("profile", profile).SetParameter("now", now).ExecuteUpdateAsync();
            await session.CreateSQLQuery("""
                INSERT INTO "AiMessage" ("Id","ConversationId","Ordinal","Role","Question","ScopeKind","ScopeSpaceId","CreatedAt")
                VALUES (gen_random_uuid(),:id,1,'user','PRIVATE-QUESTION','space',:space,:now);
                INSERT INTO "AiMessage" ("Id","ConversationId","Ordinal","Role","State","Task","CreatedAt","CompletedAt")
                VALUES (gen_random_uuid(),:id,2,'assistant','no_sources','chat.answer',:now,:now)
                """).SetParameter("id", conversation).SetParameter("space", f.SpaceId).SetParameter("now", now).ExecuteUpdateAsync();
        });
        var report = await f.As<AiQualityReport>(f.Admin, new GetAiQualityReport());
        Assert.Equal((f.SpaceId, 1L), (Assert.Single(report.NoSources).SpaceId, Assert.Single(report.NoSources).Count));
        Assert.DoesNotContain("PRIVATE-QUESTION", JsonSerializer.Serialize(report));
        Assert.Equal(2, report.Items.Count);
        Assert.Equal(2, Assert.Single(report.Items, x => x.Rating == "down").Count);
        Assert.Equal(1, Assert.Single(report.Items, x => x.Rating == "up").Count);
        Assert.DoesNotContain("PRIVATE-NOTE", JsonSerializer.Serialize(report));
        Assert.Equal(403, (await Assert.ThrowsAsync<ApiException>(() => f.As<AiQualityReport>(f.Writer, new GetAiQualityReport()))).Status);
    }

    [Fact]
    public void Csv_quotes_values_and_neutralizes_spreadsheet_formulas()
    {
        var report = new AiUsageReport(DateTime.UnixEpoch, DateTime.UnixEpoch.AddDays(7),
            [new AiUsageRow("=HYPERLINK(\"unsafe\")", "quick", "chat", 1, 0, null, null, 0, 0, 1, 1, null)])
        {
            Providers = [new AiUsageProviderRow("=HYPERLINK(\"provider\")", "cloud", "model", 1, 0, 0,
                100, 20, 1, 1, 1, 0.01m, DateTime.UnixEpoch)],
            Days = [new AiUsageDayRow(DateTime.UnixEpoch, 1, 0, 0, 100, 20, 1, 1, 1, 0.01m)]
        };
        var csv = AiReportCsv.Write(report);
        Assert.Contains("\"'=HYPERLINK(\"\"unsafe\"\")\"", csv);
        Assert.DoesNotContain("\r\n=HYPERLINK", csv);
        Assert.Contains("section,day,provider,location,model", csv);
        Assert.Contains("'=HYPERLINK(\"\"provider\"\")", csv);
    }
}
