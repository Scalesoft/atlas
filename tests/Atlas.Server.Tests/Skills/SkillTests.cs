using Atlas.Server.Features.Skills.Commands;
using Atlas.Server.Features.Skills.Queries;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageTags.Queries;
using Atlas.Server.Features.Skills;
using Atlas.Server.Platform.Identity;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Relations;
using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Skills;

/// <summary>Mapa znalostí (docs/skills-design.md): the anonymous summary, another spelling of a topic, a merge and who knows a topic.</summary>
public sealed class SkillTests
{
    [Trait("Category", "Smoke")]
    [PostgreSqlFact]
    public async Task Colleagues_count_from_three_on_another_spelling_is_one_topic_and_only_the_admin_sees_who_gave_what()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var own = Guid.NewGuid().ToString("N")[..8];
        var person = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, $"Petra {own}", $"petra-{own}@example.test");

        await f.As<ContactSkills>(f.Reader, new RateSkill(person, new RateSkillRequest($"C# {own}", 4, "shared")));
        // „c #“ is the same topic; one rating below the threshold is no number for anybody but its rater.
        await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($"c # {own}", 2, "signed", "testy")));
        var few = Assert.Single((await f.As<ContactSkills>(f.Manager, new GetContactSkills(person))).Skills);
        Assert.Equal($"C# {own}", few.Name);
        Assert.Null(few.Score);
        Assert.True(few.FewPeers);
        Assert.Equal(0, few.Peers);
        Assert.Equal(f.Writer.DisplayName, Assert.Single(few.Raters).Name);

        await f.As<ContactSkills>(f.Manager, new RateSkill(person, new RateSkillRequest($"C#   {own}", 3, "shared")));
        // Three colleagues, but beside the signed one only one anonymous rating the reader did not give: the reader knows the signed level
        // and their own, so an average would give that one away. The anonymous ones count from three of others on.
        var two = Assert.Single((await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills);
        Assert.Null(two.Score);
        Assert.True(two.FewPeers);
        Assert.Equal(4, two.Mine);
        // The anonymous ones stay anonymous: the reader sees the signed rating only, the admin every shared one with its rater.
        Assert.Equal([f.Writer.DisplayName], two.Raters.Select(x => x.Name));
        await f.As<ContactSkills>(f.Admin, new RateSkill(person, new RateSkillRequest($"C# {own}", 3, "shared")));
        Assert.Null(Assert.Single((await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills).Score);
        var shown = Assert.Single((await f.As<ContactSkills>(f.Writer, new GetContactSkills(person))).Skills);
        Assert.Equal(3.0, shown.Score);
        Assert.Equal(4, shown.Peers);
        // The admin's own rating is theirs, not a rater beside it; over the assistant (a model reads it) the admin gets no anonymous rater.
        f.Host.Channel.Current = RequestChannel.Assistant;
        try
        {
            Assert.DoesNotContain(Assert.Single((await f.As<ContactSkills>(f.Admin, new GetContactSkills(person, AiScopeOnly: true))).Skills).Raters, x => x.Visibility == "shared");
        }
        finally
        {
            f.Host.Channel.Current = RequestChannel.Web;
        }
        var admin = Assert.Single((await f.As<ContactSkills>(f.Admin, new GetContactSkills(person))).Skills);
        Assert.Equal(3, admin.Raters.Count);

        // A private rating is the rater's only and counts nowhere.
        await f.As<ContactSkills>(f.Reader, new RateSkill(person, new RateSkillRequest($"C# {own}", 5, "private")));
        Assert.Equal(2, Assert.Single((await f.As<ContactSkills>(f.Admin, new GetContactSkills(person))).Skills).Raters.Count);
        using var session = f.Host.OpenSession();
        Assert.Equal(1L, await session.CreateSQLQuery("SELECT count(*) FROM \"Tag\" WHERE \"Name\" LIKE :name").SetParameter("name", $"%{own}").UniqueResultAsync<long>());
    }

    [PostgreSqlFact]
    public async Task A_merged_topic_becomes_an_alias_its_ratings_and_page_tags_move_and_experts_are_found_by_it()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var own = Guid.NewGuid().ToString("N")[..8];
        var person = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, $"Viktor {own}", $"viktor-{own}@example.test");
        await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($"Informované souhlasy {own}", 3, "signed", "konfiguračně")));
        var other = await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($"Souhlasy pacientů {own}", 5, "signed")));
        var page = await f.PageAsync($"Souhlasy {own}");
        await f.As<PageTagState>(f.Writer, new SetPageTags(page.Id, new SetPageTagsRequest([$"Souhlasy pacientů {own}"], [])));
        var (into, from) = (other.Skills.Single(x => x.Name.StartsWith("Informované")).TagId, other.Skills.Single(x => x.Name.StartsWith("Souhlasy")).TagId);

        // The topic is on a page the reader may only read: merging it would retag that page, so it is the writer's (or an admin's).
        await Assert.ThrowsAsync<ApiException>(() => f.As<TopicView>(f.Reader, new MergeTopics(from, into)));
        var merged = await f.As<TopicView>(f.Writer, new MergeTopics(from, into));
        Assert.Equal([$"Souhlasy pacientů {own}"], merged.Aliases);
        var skill = Assert.Single((await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills);
        Assert.Equal((into, 5), (skill.TagId, skill.Raters.Single().Level));
        Assert.Equal(into, Assert.Single((await f.As<PageTagState>(f.Writer, new GetPageTags(page.Id))).Tags).Id);
        // The old name is the topic now, for a page as for a rating.
        await f.As<PageTagState>(f.Writer, new SetPageTags(page.Id, new SetPageTagsRequest([$"souhlasy  pacientu {own}"], [into])));
        Assert.Equal(into, Assert.Single((await f.As<PageTagState>(f.Writer, new GetPageTags(page.Id))).Tags).Id);

        var experts = await f.As<ExpertList>(f.Reader, new FindExperts($"souhlasy pacientů {own}"));
        Assert.Equal(into, experts.Topic!.Id);
        var expert = Assert.Single(experts.People);
        // One colleague is below the threshold: no number, but the signed rating with its name.
        Assert.Equal((person, (double?)null, 5), (expert.ContactId, expert.Skill!.Score, expert.Skill.Raters.Single().Level));
        // The page tagged with the topic is evidence only of who wrote it: the contact did not, its account does not exist.
        Assert.Equal(0, expert.Evidence);
        await Assert.ThrowsAnyAsync<Exception>(() => f.As<TopicView>(f.Reader, new MergeTopics(into, into)));
    }

    [PostgreSqlFact]
    public async Task A_merge_never_turns_a_private_rating_shared_and_a_privately_rated_topic_names_nothing_to_others()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var own = Guid.NewGuid().ToString("N")[..8];
        var person = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, $"Jana {own}", $"jana-{own}@example.test");
        var kept = await f.As<ContactSkills>(f.Reader, new RateSkill(person, new RateSkillRequest($"Kotlin {own}", 2, "shared")));
        var gone = await f.As<ContactSkills>(f.Reader, new RateSkill(person, new RateSkillRequest($"Kotlin Android {own}", 5, "private", "jen pro mě")));
        var (into, from) = (kept.Skills.Single(x => x.Name == $"Kotlin {own}").TagId, gone.Skills.Single(x => x.Name == $"Kotlin Android {own}").TagId);

        await f.As<TopicView>(f.Reader, new MergeTopics(from, into));
        // The higher level stays, and so does the private: what one kept for oneself does not enter the shared summary by a merge.
        var mine = Assert.Single((await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills);
        Assert.Equal((5, "private"), (mine.Mine, mine.MyVisibility));
        Assert.Empty((await f.As<ContactSkills>(f.Writer, new GetContactSkills(person))).Skills);

        // A shared rating of somebody's private contact: nobody else can read the contact, so its topic is nobody else's to find.
        var secret = await f.ContactAsync(f.Writer, ObjectScopes.Private, $"Soukromý {own}", $"soukromy-{own}@example.test");
        await f.As<ContactSkills>(f.Writer, new RateSkill(secret, new RateSkillRequest($"Tajné téma {own}", 4, "shared")));
        Assert.Empty((await f.As<TopicSuggestions>(f.Reader, new SuggestTopics($"Tajné téma {own}"))).Items);
        Assert.Single((await f.As<TopicSuggestions>(f.Writer, new SuggestTopics($"Tajné téma {own}"))).Items);
    }

    [PostgreSqlFact]
    public async Task One_anonymous_label_lists_nothing_the_person_or_an_admin_removes_a_topic_and_a_hidden_parent_is_not_named()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var own = Guid.NewGuid().ToString("N")[..8];
        var person = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, $"Karel {own}", $"karel-{own}@example.test");
        // One anonymous rating is a label on somebody nobody answers for: the others do not see the topic at all.
        await f.As<ContactSkills>(f.Reader, new RateSkill(person, new RateSkillRequest($"Nálepka {own}", 1, "shared")));
        Assert.Empty((await f.As<ContactSkills>(f.Writer, new GetContactSkills(person))).Skills);
        Assert.Empty((await f.As<ExpertList>(f.Writer, new FindExperts($"Nálepka {own}"))).People);

        // Neither a colleague nor the rater removes it for everyone; a wiki admin (or the person) does.
        var everyone = new RateSkillRequest($"Nálepka {own}", null, Everyone: true);
        await Assert.ThrowsAsync<ApiException>(() => f.As<ContactSkills>(f.Writer, new RateSkill(person, everyone)));
        Assert.True((await f.As<ContactSkills>(f.Admin, new GetContactSkills(person))).Moderate);
        await f.As<ContactSkills>(f.Admin, new RateSkill(person, everyone));
        Assert.Empty((await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills);

        // A broader topic only the writer may know of (their private rating) is no parent for the reader.
        await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($"Tajný obor {own}", 3, "private")));
        var rated = await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($"Veřejné {own}", 4, "signed")));
        var (narrow, broad) = (rated.Skills.Single(x => x.Name == $"Veřejné {own}").TagId, rated.Skills.Single(x => x.Name == $"Tajný obor {own}").TagId);
        await f.As<TopicView>(f.Writer, new LinkTopics(narrow, broad));
        Assert.Single((await f.As<ExpertList>(f.Writer, new FindExperts($"Veřejné {own}"))).Topic!.Parents);
        Assert.Empty((await f.As<ExpertList>(f.Reader, new FindExperts($"Veřejné {own}"))).Topic!.Parents);
        Assert.Equal($"Tajný obor {own}", (await f.As<ContactSkills>(f.Writer, new GetContactSkills(person))).Skills.Single(x => x.TagId == narrow).Group);
        Assert.Null(Assert.Single((await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills).Group);
    }

    [PostgreSqlFact]
    public async Task Topics_link_as_part_of_or_related_and_the_graph_shows_people_topics_and_the_links()
    {
        await using var f = await RelationsFixture.SharedAsync();
        var own = Guid.NewGuid().ToString("N")[..8];
        var person = await f.ContactAsync(f.Writer, ObjectScopes.Wiki, $"Lucie {own}", $"lucie-{own}@example.test");
        var rated = await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($"C# {own}", 4, "signed")));
        rated = await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($".NET {own}", 3, "signed")));
        rated = await f.As<ContactSkills>(f.Writer, new RateSkill(person, new RateSkillRequest($"Azure {own}", 2, "signed")));
        var id = (string name) => rated.Skills.Single(x => x.Name == $"{name} {own}").TagId;
        await f.As<TopicView>(f.Reader, new LinkTopics(id("C#"), id(".NET")));
        // Related either way round is one link; the narrower topic carries its group.
        await f.As<TopicView>(f.Reader, new LinkTopics(id("Azure"), id(".NET"), Kind: "related"));
        var dotnet = await f.As<TopicView>(f.Reader, new LinkTopics(id(".NET"), id("Azure"), Kind: "related"));
        Assert.Equal([$"C# {own}"], dotnet.Children.Select(x => x.Name));
        Assert.Equal([$"Azure {own}"], dotnet.Related!.Select(x => x.Name));
        Assert.Equal($".NET {own}", (await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills.Single(x => x.TagId == id("C#")).Group);

        var graph = await f.As<SkillGraph>(f.Reader, new GetSkillGraph());
        Assert.Contains(graph.Nodes, x => x.Id == person && x.Kind == "person");
        Assert.Contains(graph.Links, x => (x.Source, x.Target, x.Kind) == (id("C#"), id(".NET"), "part_of"));
        Assert.Single(graph.Links, x => x.Kind == "related" && new[] { x.Source, x.Target }.Order().SequenceEqual(new[] { id("Azure"), id(".NET") }.Order()));
        // One colleague's rating is no number for the reader.
        Assert.Null(graph.Links.Single(x => (x.Source, x.Target) == (person, id("C#"))).Level);
    }

    [PostgreSqlFact]
    public async Task The_migration_runs_over_existing_tags_and_again_without_a_change()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_skills_");
        await ExecuteAsync(schema.ConnectionString, """
            DROP TABLE "SkillRating"; DROP TABLE "TagRelation"; DROP TABLE "TagAlias"; ALTER TABLE "Tag" DROP COLUMN "MergedIntoId";
            DELETE FROM "SchemaVersion" WHERE "Version" = 2026112310;
            INSERT INTO "UserAccount"("Id","Username","DisplayName","IsWikiAdmin","IsActive","SecurityStamp") VALUES (@admin,'admin','Admin',true,true,'stamp');
            INSERT INTO "Tag"("Id","Name","NormalizedName","CreatedAt","CreatedById") VALUES (@tag,'C#','c#',now(),@admin);
            """, ("admin", Guid.NewGuid()), ("tag", Guid.NewGuid()));

        await AtlasMigrations.MigrateAsync(schema.ConnectionString);

        Assert.Equal(1L, await ScalarAsync(schema.ConnectionString, """SELECT count(*) FROM "Tag" WHERE "Name" = 'C#' AND "MergedIntoId" IS NULL"""));
        Assert.Equal(0L, await ScalarAsync(schema.ConnectionString, """SELECT (SELECT count(*) FROM "SkillRating") + (SELECT count(*) FROM "TagAlias") + (SELECT count(*) FROM "TagRelation")"""));
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteScalarAsync();
    }

    private static Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters) => ScalarAsync(connectionString, sql, parameters);
}
