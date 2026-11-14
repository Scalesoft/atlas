using Atlas.Server.Features.Ai.Availability;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>A3: the circuit breaker of each profile kind (AI-25), without a database and on a fake clock.</summary>
public sealed class AiAvailabilityTests
{
    private const string UpstreamText = "upstream says TAJNE-503";

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task Two_consecutive_failures_make_a_profile_unavailable_and_the_first_success_restores_it()
    {
        using var ai = new AvailabilityHost();
        var profile = FakeAi.Profile(AiProfileKind.Embeddings);
        await ai.ConfigureAsync(true, profile);
        var availability = ai.Availability;
        // Not verified yet (O13): unavailable until a probe or a call succeeds.
        Assert.Equal(AiProfileState.Unknown, availability.Health(AiProfileKind.Embeddings).State);
        Assert.False(availability.IsAvailable(AiProfileKind.Embeddings));

        using var embeddings = ai.Fake.Embeddings(profile, AiTaskCatalog.SearchEmbedQuery);
        await embeddings.GenerateAsync(["síť VPN"]);
        var up = ai.Clock.GetUtcNow().UtcDateTime;
        Assert.True(availability.IsAvailable(AiProfileKind.Embeddings));
        Assert.Equal((AiProfileState.Available, (DateTime?)up, (DateTime?)up), Health(availability, x => (x.State, x.Since, x.LastSuccessAt)));

        // One failure is not an outage.
        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Http5xx, UpstreamText));
        ai.Clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<AiCallException>(() => embeddings.GenerateAsync(["síť VPN"]));
        Assert.Equal((AiProfileState.Available, AiOutcome.Http5xx), Health(availability, x => (x.State, x.LastErrorCode)));

        // The second in a row is: unavailable since that failure, with its code and never the upstream's text.
        ai.Clock.Advance(TimeSpan.FromSeconds(5));
        var down = ai.Clock.GetUtcNow().UtcDateTime;
        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.Connection, UpstreamText));
        await Assert.ThrowsAsync<AiCallException>(() => embeddings.GenerateAsync(["síť VPN"]));
        Assert.Equal((AiProfileState.Unavailable, (DateTime?)down, AiOutcome.Connection, (DateTime?)up),
            Health(availability, x => (x.State, x.Since, x.LastErrorCode, x.LastSuccessAt)));
        Assert.False(availability.IsAvailable(AiProfileKind.Embeddings));
        ai.Clock.Advance(TimeSpan.FromSeconds(5));
        availability.Failure(profile, AiOutcome.Timeout);
        Assert.Equal(((DateTime?)down, AiOutcome.Timeout), Health(availability, x => (x.Since, x.LastErrorCode)));

        // Another revision of the kind (a candidate generation's, a stale snapshot) never moves the current one's state.
        var other = profile with { RevisionId = Guid.NewGuid() };
        availability.Success(other);
        Assert.Equal(AiProfileState.Unavailable, availability.Health(AiProfileKind.Embeddings).State);

        // The first success restores it.
        ai.Clock.Advance(TimeSpan.FromSeconds(30));
        var back = ai.Clock.GetUtcNow().UtcDateTime;
        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        await embeddings.GenerateAsync(["síť VPN"]);
        Assert.Equal((AiProfileState.Available, (DateTime?)back, (DateTime?)back), Health(availability, x => (x.State, x.Since, x.LastSuccessAt)));
        Assert.True(availability.IsAvailable(AiProfileKind.Embeddings));
        Assert.DoesNotContain(ai.Logs.Messages, line => line.Contains("TAJNE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_success_between_failures_resets_the_count_and_bad_requests_or_cancellations_are_not_failures()
    {
        using var ai = new AvailabilityHost();
        var profile = FakeAi.Profile(AiProfileKind.Embeddings);
        await ai.ConfigureAsync(true, profile);
        var availability = ai.Availability;
        availability.Success(profile);

        // 1. A success between two failures: never two in a row.
        availability.Failure(profile, AiOutcome.Timeout);
        availability.Success(profile);
        availability.Failure(profile, AiOutcome.Timeout);
        Assert.Equal(AiProfileState.Available, availability.Health(AiProfileKind.Embeddings).State);
        availability.Success(profile);

        // 2. Atlas's own bad request is no verdict on the server: neither a failure nor a success.
        using var embeddings = ai.Fake.Embeddings(profile, AiTaskCatalog.SearchEmbedQuery);
        availability.Failure(profile, AiOutcome.Timeout);
        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Error(AiOutcome.BadRequest));
        Assert.Equal(AiOutcome.BadRequest, (await Assert.ThrowsAsync<AiCallException>(() => embeddings.GenerateAsync(["dotaz"]))).Code);
        availability.Failure(profile, AiOutcome.InvalidOutput);
        Assert.Equal((AiProfileState.Available, AiOutcome.Timeout), Health(availability, x => (x.State, x.LastErrorCode)));

        // 3. The caller giving up (an aborted search) is not the server's failure either.
        ai.Fake.SetMode(AiProfileKind.Embeddings, FakeAiMode.Healthy);
        var hold = ai.Fake.Hold(AiProfileKind.Embeddings);
        using (var aborted = new CancellationTokenSource())
        {
            var pending = embeddings.GenerateAsync(["dotaz"], cancellationToken: aborted.Token);
            await hold.Entered;
            await aborted.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        hold.Release();
        availability.Failure(profile, AiOutcome.Canceled);
        Assert.Equal(AiProfileState.Available, availability.Health(AiProfileKind.Embeddings).State);

        // The earlier timeout still counts: the next server failure (a refused key, O14) is the second in a row.
        availability.Failure(profile, AiOutcome.Auth);
        Assert.Equal((AiProfileState.Unavailable, AiOutcome.Auth), Health(availability, x => (x.State, x.LastErrorCode)));
    }

    private static T Health<T>(AiAvailability availability, Func<AiProfileHealth, T> select) => select(availability.Health(AiProfileKind.Embeddings));
}
