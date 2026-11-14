using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Contracts;
using Atlas.Server.Features.Ai.Settings.Queries;
using Xunit;

namespace Atlas.Server.Tests.Ai;

public sealed class AiFeatureSettingsTests
{
    [PostgreSqlFact]
    public async Task Defaults_come_from_the_catalog_and_cloud_permission_applies_only_to_working_text_tasks()
    {
        await using var f = await AiSettingsFixture.CreateAsync();
        var initial = await f.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        Assert.Null(initial.Version);
        // Only switchable tasks, in catalog order; indexing and the admin tests are governed elsewhere.
        Assert.Equal(
        [
            new(AiTaskCatalog.SearchEmbedQuery, "search", true, "embeddings", null),
            new(AiTaskCatalog.PageRelated, "read", true, "embeddings", null),
            new(AiTaskCatalog.EditorLink, "write", true, "embeddings", false),
            new(AiTaskCatalog.PageCreateSuggest, "create", true, "embeddings", false),
            new(AiTaskCatalog.SearchAnswer, "search", true, "chat", null, Limitable: true),
            new(AiTaskCatalog.ChatAnswer, "read", true, "chat", null, Limitable: true),
            new(AiTaskCatalog.PageSummary, "read", true, "chat", null, Limitable: true),
            new(AiTaskCatalog.EditorIntent, "write", false, "quick", false),
            new(AiTaskCatalog.EditorTransform, "write", false, "quick", false),
            new(AiTaskCatalog.EditorOutline, "write", false, "quick", false),
            new(AiTaskCatalog.PublishCheck, "write", false, "quick", false),
            new(AiTaskCatalog.ChangeSummary, "write", false, "quick", false),
            new AiFeatureItem(AiTaskCatalog.TagSuggest, "write", false, "quick", false),
            new(AiTaskCatalog.AssistantAgent, "read", true, "chat", null, Limitable: true),
            new(AiTaskCatalog.AssistantWorkingText, "write", true, "chat", false),
            new(AiTaskCatalog.AssistantArrange, "write", true, "chat", null),
            new(AiTaskCatalog.SearchRerank, "search", true, "rerank", null),
        ], initial.Items);

        var saved = await f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.EditorLink, false, true)], null)));
        Assert.NotNull(saved.Version);
        Assert.Equal(new AiFeatureItem(AiTaskCatalog.EditorLink, "write", false, "embeddings", true), saved.Items[2]);
        // Tasks the request did not list keep their value.
        Assert.Equal(initial.Items.Where(x => x.Task != AiTaskCatalog.EditorLink), saved.Items.Where(x => x.Task != AiTaskCatalog.EditorLink));
        await SameAsync(f, saved);

        await AiSettingsFixture.Denied(400, () => f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.SearchEmbedQuery, true, true)], saved.Version))));
        await AiSettingsFixture.Denied(400, () => f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new("page.unknown", true, null)], saved.Version))));
        await AiSettingsFixture.Denied(400, () => f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.IndexEmbed, false, null)], saved.Version))));
        await AiSettingsFixture.Denied(409, () => f.As<AiFeaturesView>(f.Admin, new SaveAiFeatures(new([new(AiTaskCatalog.PageRelated, false, null)], null))));
        await SameAsync(f, saved);
    }

    private static async Task SameAsync(AiSettingsFixture f, AiFeaturesView expected)
    {
        var actual = await f.As<AiFeaturesView>(f.Admin, new GetAiFeatures());
        Assert.Equal(expected.Items, actual.Items);
        Assert.Equal(expected.Version, actual.Version);
    }
}
