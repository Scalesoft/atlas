using Atlas.Server.Features.PersonalApi;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

public sealed class PersonalApiCredentialThrottleTests
{
    [Fact]
    public void Only_failures_spend_the_budget_of_their_own_address()
    {
        using var throttle = new PersonalApiCredentialThrottle();
        // Asking spends nothing, however often a valid token asks.
        for (var request = 0; request < 3 * PersonalApiCredentialThrottle.FailureLimit; request++)
        {
            Assert.True(throttle.IsOpen("10.0.0.1", out _));
        }
        for (var failure = 0; failure < PersonalApiCredentialThrottle.FailureLimit; failure++)
        {
            Assert.True(throttle.IsOpen("10.0.0.1", out _));
            throttle.RecordFailure("10.0.0.1");
        }
        Assert.False(throttle.IsOpen("10.0.0.1", out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), PersonalApiCredentialThrottle.Window);
        Assert.True(throttle.IsOpen("10.0.0.2", out _));
    }
}
