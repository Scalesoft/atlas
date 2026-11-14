using Atlas.Server.Features.Assistant.Domain;
using Atlas.Server.Features.Assistant.Feedback.Commands;
using Atlas.Server.Features.Assistant.Feedback.Contracts;
using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Atlas.Server.Features.SearchGaps.Commands;
using Atlas.Server.Features.SearchGaps.Contracts;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>Plan phase 3 Q5, R9: "Nahlásit chybějící obsah" writes the question to the gaps of its space as <c>reported</c>, the
/// strongest reason of a person, query and day, under the same k-anonymity and policy as a zero-result query.</summary>
public sealed class ReportMissingTests
{
    private const string Question = "Jak nastavím tiskárnu?";
    private static readonly DateOnly s_today = DateOnly.FromDateTime(DateTime.UtcNow);

    [PostgreSqlFact]
    public async Task Reports_reach_the_space_gaps_as_reported_and_respect_k_anonymity()
    {
        await using var f = await FeedbackFixture.CreateAsync();
        var (u1, u2, u3) = (f.Readers[0], f.Readers[1], f.Readers[2]);
        // A question of this test alone: the gap log of the shared host holds every test's questions.
        var question = Question + " " + Guid.NewGuid().ToString("N")[..8];
        var normalized = SearchText.Normalize(question);
        var mine = $"""WHERE "NormalizedQuery"='{normalized}'""";

        // Reader 1 found nothing first and reports afterwards; reader 2 reports and a zero-result search follows. Either way
        // the stronger reason stays. Reader 3 only got "Bez podkladů" (written by the answer flow, O16).
        var first = await f.AnswerAsync(u1, question, AiMessageState.NoSources);
        var second = await f.AnswerAsync(u2, question, AiMessageState.NoSources);
        await f.Record(Miss(f, u1, question, SearchGapReason.Zero));
        await Report(f, u1, first.AnswerId);
        await Report(f, u2, second.AnswerId);
        await f.Record(Miss(f, u2, question, SearchGapReason.Zero));
        Assert.Equal(["reported", "reported"], await f.Strings($"""SELECT "Reason" FROM "SearchQueryLog" {mine} ORDER BY "Id" """));

        // Two people are below the threshold: nothing is shown, whatever the reason.
        Assert.Empty((await f.Gaps()).Items);

        await f.Record(Miss(f, u3, question, SearchGapReason.NoAnswer));
        var gap = Assert.Single((await f.Gaps()).Items);
        Assert.Equal((normalized, 3, 2, 1), (gap.NormalizedQuery, gap.Users, gap.Reported, gap.Unanswered));
        // The question is stored in the space of its scope; the log holds the day, a key and the reasons, not the person.
        Assert.Equal([f.SpaceId.ToString()], (await f.Strings($"""SELECT DISTINCT CAST("SpaceId" AS text) FROM "SearchQueryLog" {mine}""")));

        // Reporting again is idempotent.
        await Report(f, u1, first.AnswerId);
        Assert.Equal(3L, (await f.Strings($"""SELECT CAST(count(*) AS text) FROM "SearchQueryLog" {mine}""")).Select(long.Parse).Single());
    }

    [PostgreSqlFact]
    public async Task Only_an_answer_without_sources_or_rated_missing_info_can_be_reported_by_its_owner()
    {
        await using var f = await FeedbackFixture.CreateAsync();
        var owner = f.Readers[0];
        var answered = await f.AnswerAsync(owner, Question, AiMessageState.Answered);
        var preparing = await f.AnswerAsync(owner, "Kde parkovat?", AiMessageState.Preparing);

        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Report(f, f.Readers[1], answered.AnswerId))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiException>(() => Report(f, owner, answered.QuestionId))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Report(f, owner, answered.AnswerId))).Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Report(f, owner, preparing.AnswerId))).Status);

        // A thumb down for another reason does not open it; "Chybí informace" does.
        f.Host.Actor.Current = owner;
        await f.Host.InvokeAsync(new RateAiMessage(answered.AnswerId, new AiFeedbackRequest("down", AiFeedbackReason.Inaccurate, null)));
        Assert.Equal(409, (await Assert.ThrowsAsync<ApiException>(() => Report(f, owner, answered.AnswerId))).Status);
        await f.Host.InvokeAsync(new RateAiMessage(answered.AnswerId, new AiFeedbackRequest("down", AiFeedbackReason.MissingInfo, null)));
        await Report(f, owner, answered.AnswerId);
        Assert.Equal(["reported"], await f.Strings($"""SELECT "Reason" FROM "SearchQueryLog" WHERE "SpaceId"='{f.SpaceId}'"""));
    }

    [PostgreSqlFact]
    public async Task The_report_keeps_the_d9_policy_and_the_space_only_when_the_person_reaches_it()
    {
        await using var f = await FeedbackFixture.CreateAsync();
        var reader = f.Readers[0];
        var outsider = await f.Host.SeedUserAsync("outsider");
        f.Host.Actor.Current = reader;

        // Too short, too long, or with a secret-like word: refused, nothing stored.
        foreach (var refused in new[] { "ab", new string('a', 10) + " " + new string('b', 120), "heslo Abcdefgh1234567" })
        {
            Assert.Equal(400, (await Assert.ThrowsAsync<ApiException>(() => f.Host.InvokeAsync(new ReportMissingSearch(new ReportMissingSearchRequest(refused, f.SpaceId, null))))).Status);
        }
        Assert.Empty(await f.Strings($"""SELECT "Reason" FROM "SearchQueryLog" WHERE "SpaceId"='{f.SpaceId}'"""));

        // The omnisearch rescue: the filter wins over the context; a space the person does not reach is dropped, not refused.
        // The query is this test's alone: the gap log of the shared host holds every test's queries.
        var query = "Přístup do VPN " + Guid.NewGuid().ToString("N")[..8];
        await f.Host.InvokeAsync(new ReportMissingSearch(new ReportMissingSearchRequest(query, null, f.SpaceId)));
        f.Host.Actor.Current = outsider;
        await f.Host.InvokeAsync(new ReportMissingSearch(new ReportMissingSearchRequest(query, f.SpaceId, null)));
        Assert.Equal(["reported|" + f.SpaceId, "reported|"], await f.Strings($"""
            SELECT "Reason" || '|' || coalesce(CAST("SpaceId" AS text),'') FROM "SearchQueryLog" WHERE "NormalizedQuery"='{SearchText.Normalize(query)}'
            ORDER BY "SpaceId" NULLS LAST
            """));
    }

    private static SearchMiss Miss(FeedbackFixture f, UserAccount user, string question, string reason)
        => new(user.Id, f.SpaceId, SearchText.Normalize(question), SearchMissPolicy.Display(question), s_today, reason);

    private static Task Report(FeedbackFixture f, UserAccount actor, Guid messageId)
    {
        f.Host.Actor.Current = actor;
        return f.Host.InvokeAsync(new ReportMissingContent(messageId));
    }
}
