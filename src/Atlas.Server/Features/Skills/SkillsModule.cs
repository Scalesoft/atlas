using Atlas.Server.Features.Skills.Commands;
using Atlas.Server.Features.Skills.Queries;
using Atlas.Server.Features.ApiActivity;
using Atlas.Server.Features.PersonalApi;
using FluentMigrator;
using Wolverine;

namespace Atlas.Server.Features.Skills;

[Migration(2026112310, "Kdo co umí: topic aliases, merges and links, people's skill ratings")]
public sealed class SkillsMigration() : EmbeddedSchemaMigration("2026112310_skills.sql");

/// <summary>Kdo co umí (docs/skills-design.md): the web's routes and, for AI parity, the same under /api/v1 (reads with the read scope,
/// rating, merging and linking with the write scope: they change what other readers see at once).</summary>
public static class SkillsModule
{
    public static IServiceCollection AddSkills(this IServiceCollection services) => services.AddScoped<SkillStore>();

    public static void MapSkills(this WebApplication app)
    {
        Map(app.MapGroup("/api").RequireAuthorization(), false);
        Map(app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(PersonalApiEndpoints.ReadPolicy).RequireRateLimiting("personal-api"), true);
    }

    private static void Map(RouteGroupBuilder group, bool personalApi)
    {
        var read = group.MapGet("/contacts/{id:guid}/skills", async (Guid id, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ContactSkills>(new GetContactSkills(id), ct)))
            .Produces<ContactSkills>().ProducesProblem(404)
            .WithSummary("What a person (a directory contact) knows as the reader may see it: per topic the score 1-5 (own shared estimate and the " +
                "colleagues' average from 3 ratings on), the reader's own rating, signed ratings, notes and how many readable pages of the topic the person wrote.");
        var rate = group.MapPut("/contacts/{id:guid}/skills", async (Guid id, RateSkillRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ContactSkills>(new RateSkill(id, request), ct)))
            .Produces<ContactSkills>().ProducesProblem(400).ProducesProblem(404)
            .WithSummary("Rate a person in a topic named by text (an existing topic, its alias or another spelling of it, else a new one): level 1-5 or null " +
                "to remove, visibility private | shared (anonymous) | signed, an optional note. Others see it at once.");
        var experts = group.MapGet("/skills/experts", async (string topic, string? context, string? role, int? limit, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<ExpertList>(new FindExperts(topic, context, role, limit ?? 10), ct)))
            .Produces<ExpertList>().ProducesProblem(400).RequireRateLimiting("search")
            .WithSummary("Who knows a topic (and its narrower topics): people by score and readable pages of the topic they wrote or are mentioned on; " +
                "context (a space, a tag or an organization) ranks its people first, role filters by the contact's function. Also similar topics.");
        var topics = group.MapGet("/skills/topics", async (string? q, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<TopicSuggestions>(new SuggestTopics(q), ct)))
            .Produces<TopicSuggestions>().RequireRateLimiting("search")
            .WithSummary("Topics by part of a name or alias, the closest first, each with how many people know it.");
        var merge = group.MapPost("/skills/topics/{id:guid}/merge", async (Guid id, MergeTopicsRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<TopicView>(new MergeTopics(id, request.IntoId), ct)))
            .Produces<TopicView>().ProducesProblem(400).ProducesProblem(404)
            .WithSummary("Merge a topic into another: its name becomes an alias, its ratings, links and page tags move. Cannot be undone by a call.");
        var link = group.MapPut("/skills/topics/{id:guid}/parents", async (Guid id, LinkTopicsRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<TopicView>(new LinkTopics(id, request.ParentId, request.Remove, request.Kind ?? "part_of"), ct)))
            .Produces<TopicView>().ProducesProblem(400).ProducesProblem(404)
            .WithSummary("Make a topic part of a broader one (kind part_of, C# of .NET) or related to another (kind related), or with remove not any more.");
        var graph = group.MapGet("/skills/graph", async (Guid? spaceId, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<SkillGraph>(new GetSkillGraph(spaceId), ct)))
            .Produces<SkillGraph>().RequireRateLimiting("search")
            .WithSummary("Who knows what as a graph: people and the topics they know (the level the reader may see), and the topics' part_of and related links; spaceId keeps the topics of that space's pages.");
        if (!personalApi)
        {
            return;
        }
        read.WithName("GetContactSkills");
        experts.WithName("FindExperts");
        topics.WithName("SuggestTopics");
        graph.WithName("GetSkillGraph");
        rate.WithName("RateSkill").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.SkillRate);
        merge.WithName("MergeTopics").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.TopicMerge);
        link.WithName("LinkTopics").RequireAuthorization(PersonalApiEndpoints.WritePolicy).AuditApiWrite(ApiOperations.TopicLink);
    }
}
