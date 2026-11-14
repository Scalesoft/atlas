using Atlas.Server.Features.KnowledgeMaintenance.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Atlas.Server.Features.KnowledgeMaintenance;
using Atlas.Server.Platform.Retention;
using Atlas.Server.Tests.AiIndex;
using Xunit;

namespace Atlas.Server.Tests.KnowledgeMaintenance;

public sealed class AiInteractionTests
{
    [PostgreSqlFact]
    public async Task Events_aggregate_by_day_without_content_and_unknown_categories_are_refused()
    {
        await using var f = await IndexFixture.CreateAsync();
        Assert.False(f.Host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(RecordAiInteraction)).IsEnabled(LogLevel.Error));
        Assert.DoesNotContain("PRIVATE-CONTENT", new RecordAiInteraction("PRIVATE-CONTENT", "PRIVATE-CONTENT", "PRIVATE-CONTENT").ToString());
        var request = new RecordAiInteraction("editor.transform", "shorten", "applied");
        await f.As<bool>(f.Writer, request);
        await f.As<bool>(f.Writer, request);
        await f.As<bool>(f.Writer, request with { Outcome = "shown" });
        var report = await f.As<AiQualityReport>(f.Admin, new GetAiQualityReport());
        Assert.Equal(2L, Assert.Single(report.Interactions, x => x.Outcome == "applied").Count);
        Assert.Equal(1L, Assert.Single(report.Interactions, x => x.Outcome == "shown").Count);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.As<bool>(f.Writer, request with { Kind = "PRIVATE-ARBITRARY-TEXT" }))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.As<bool>(f.Writer, request with { Task = "admin.eval" }))).Status);
        // 90 days are kept by the hourly retention (ARCH3-1), not by the writer.
        await f.Host.ExecuteAsync("""UPDATE "AiInteractionDaily" SET "Day"="Day"-91 WHERE "Outcome"='shown' AND :id IS NOT NULL""", Guid.Empty);
        await f.Host.Services.GetRequiredService<OperationalRetention>().RunAsync(default);
        Assert.Equal(1, await f.CountAsync("""SELECT count(*) FROM "AiInteractionDaily" """));
    }
}
