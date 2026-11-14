using System.Net;
using System.Text.Json.Nodes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>What HTTP adds to the conversation endpoints (plan phase 3 §8, §20): routes, binding of <c>before</c>, the JSON shape for
/// W10/W11, status codes, no-store, and that they work without any AI configured (návrh §2a point 7: the history is the
/// user's data, and deleting it is the user's right).</summary>
public sealed class ConversationHttpTests
{
    [PostgreSqlFact]
    public async Task Conversation_endpoints_work_without_ai_and_bind()
    {
        await using var site = await AtlasHttpSite.CreateIsolatedAsync("atlas_conversations_http_"); // an installation without AI: the AI switch and profiles are rows of the whole installation, and other tests on the shared site configure them
        using var client = site.Client();
        var owner = Guid.Parse((await site.CompleteSetupAsync(client))["id"]!.GetValue<string>());
        var profile = Guid.NewGuid();
        await site.ExecuteAsync("""
            INSERT INTO "AiProfileRevision"("Id","Kind","Enabled","Provider","Location","BaseUrl","Model","TimeoutSeconds","MaxConcurrency","CreatedById","CreatedAt")
            VALUES (@profile,'chat',true,'openai-compatible','company','http://lmstudio.example:1234/v1','gemma-4',120,2,@owner,now())
            """, ("profile", profile), ("owner", owner));
        // Recent: the host runs the retention service, which purges an unlisted conversation after a day.
        var now = DateTime.UtcNow;
        var at = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddHours(-2);
        var listed = await SeedAsync(site, owner, profile, true, "Kdo schvaluje dovolenou?", at);
        var unlisted = await SeedAsync(site, owner, profile, false, "Kde je jídelna?", at.AddHours(1));
        var stamp = at.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        var response = await client.GetAsync("/api/ai/conversations");
        Assert.True(response.Headers.CacheControl?.NoStore);
        var list = await AtlasHttpSite.Json(response, HttpStatusCode.OK);
        Assert.Null(list["next"]);
        var item = Assert.Single(list["items"]!.AsArray())!;
        Assert.Equal(
            $$$"""{"id":"{{{listed}}}","title":"Kdo schvaluje dovolenou?","available":true,"lastActivityAt":"{{{stamp}}}","provenance":{"location":"company","provider":"LM Studio","model":"gemma-4"}}""",
            item.ToJsonString());
        Assert.Empty((await AtlasHttpSite.Json(await client.GetAsync("/api/ai/conversations?before=" + stamp), HttpStatusCode.OK))["items"]!.AsArray());
        Assert.Single((await AtlasHttpSite.Json(await client.GetAsync("/api/ai/conversations?before=" + Uri.EscapeDataString(at.AddSeconds(1).AddHours(2).ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "+02:00")), HttpStatusCode.OK))["items"]!.AsArray());

        var detail = await AtlasHttpSite.Json(await client.GetAsync($"/api/ai/conversations/{listed}"), HttpStatusCode.OK);
        Assert.Equal((listed.ToString(), true, false, false), (detail["id"]!.GetValue<string>(), detail["available"]!.GetValue<bool>(),
            detail["canContinue"]!.GetValue<bool>(), detail["outdated"]!.GetValue<bool>()));
        var messages = detail["messages"]!.AsArray();
        Assert.Equal(["user", "assistant"], messages.Select(x => x!["role"]!.GetValue<string>()));
        Assert.Equal(("Kdo schvaluje dovolenou?", "wiki"), (messages[0]!["question"]!.GetValue<string>(), messages[0]!["scope"]!["kind"]!.GetValue<string>()));
        Assert.Equal(("no_sources", 0, 0), (messages[1]!["state"]!.GetValue<string>(), messages[1]!["sentences"]!.AsArray().Count,
            messages[1]!["sources"]!.AsArray().Count));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/ai/conversations/not-an-id")).StatusCode);

        // S6 continues the omnisearch's conversation into the list, newest activity first.
        var continued = await AtlasHttpSite.Json(await AtlasHttpSite.SendAsync(client, HttpMethod.Post, $"/api/ai/conversations/{unlisted}/continue"), HttpStatusCode.OK);
        Assert.Equal(unlisted.ToString(), continued["id"]!.GetValue<string>());
        Assert.Equal([unlisted.ToString(), listed.ToString()], (await AtlasHttpSite.Json(await client.GetAsync("/api/ai/conversations"), HttpStatusCode.OK))["items"]!
            .AsArray().Select(x => x!["id"]!.GetValue<string>()));

        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Delete, $"/api/ai/conversations/{listed}")).StatusCode);
        var missing = await AtlasHttpSite.SendAsync(client, HttpMethod.Delete, $"/api/ai/conversations/{listed}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NoContent, (await AtlasHttpSite.SendAsync(client, HttpMethod.Delete, "/api/ai/conversations")).StatusCode);
        Assert.Empty((await AtlasHttpSite.Json(await client.GetAsync("/api/ai/conversations"), HttpStatusCode.OK))["items"]!.AsArray());
    }

    /// <summary>A conversation with one question the retrieval found nothing for (no_sources): no page needed.</summary>
    private static async Task<Guid> SeedAsync(AtlasHttpSite site, Guid owner, Guid profile, bool listed, string question, DateTime at)
    {
        var id = Guid.NewGuid();
        await site.ExecuteAsync("""
            INSERT INTO "AiConversation"("Id","OwnerId","Origin","Listed","ChatProfileRevisionId","ChatCompatibility","CreatedAt","LastActivityAt","Version")
            VALUES (@id,@owner,CASE WHEN @listed THEN 'assistant' ELSE 'search' END,@listed,@profile,repeat('a',64),@at,@at,1);
            INSERT INTO "AiMessage"("Id","ConversationId","Ordinal","Role","Question","ScopeKind","CreatedAt") VALUES (gen_random_uuid(),@id,1,'user',@question,'wiki',@at);
            INSERT INTO "AiMessage"("Id","ConversationId","Ordinal","Role","State","Task","CreatedAt","CompletedAt")
            VALUES (gen_random_uuid(),@id,2,'assistant','no_sources','search.answer',@at,@at);
            """, ("id", id), ("owner", owner), ("listed", listed), ("profile", profile), ("question", question), ("at", at));
        return id;
    }
}
