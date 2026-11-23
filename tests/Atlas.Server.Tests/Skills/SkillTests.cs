using Atlas.Server.Features.Skills.Commands;
using Atlas.Server.Features.Skills.Queries;
using Atlas.Server.Features.PageTags;
using Atlas.Server.Features.PageTags.Queries;
using Atlas.Server.Features.Skills;
using Atlas.Server.Platform.Resources;
using Atlas.Server.Tests.Relations;
using Npgsql;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Skills;

/// <summary>Kdo co umí (docs/skills-design.md): the anonymous summary, another spelling of a topic, a merge and who knows a topic.</summary>
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
        var shown = Assert.Single((await f.As<ContactSkills>(f.Reader, new GetContactSkills(person))).Skills);
        Assert.Equal(3.0, shown.Score);
        Assert.Equal(3, shown.Peers);
        Assert.Equal(4, shown.Mine);
        // The anonymous ones stay anonymous: the reader sees the signed rating only, the admin every shared one with its rater.
        Assert.Equal([f.Writer.DisplayName], shown.Raters.Select(x => x.Name));
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

        var merged = await f.As<TopicView>(f.Reader, new MergeTopics(from, into));
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
    public async Task The_migration_runs_over_existing_tags_and_again_without_a_change()
    {
        await using var schema = await DisposableSchema.CreateMigratedAsync("atlas_skills_");
        await ExecuteAsync(schema.ConnectionString, """
            DROP TABLE "SkillRating"; DROP TABLE "TagRelation"; DROP TABLE "TagAlias"; ALTER TABLE "Tag" DROP COLUMN "MergedIntoId";
            DELETE FROM "SchemaVersion" WHERE "Version" = 2026112300;
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
