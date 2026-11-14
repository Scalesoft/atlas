using Xunit;
using Atlas.Server.Features.Auth.Domain;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Contracts;

namespace Atlas.Server.Tests;

public sealed class BuiltinAuthenticationTests
{
    [Fact]
    public void Guessing_one_account_does_not_block_other_people_behind_the_same_office_IP()
    {
        var throttle = new BuiltinLoginThrottle();
        for (var attempt = 0; attempt < BuiltinLoginThrottle.FailureLimit; attempt++)
        {
            using var guess = throttle.TryBegin("alice", out _);
            Assert.NotNull(guess);
            guess.Fail();
        }
        Assert.Null(throttle.TryBegin("ALICE", out var retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromSeconds(1), BuiltinLoginThrottle.Window);
        using var otherAccount = throttle.TryBegin("bob", out _);
        Assert.NotNull(otherAccount);
    }

    /// <summary>SEC-A-3: anonymous guesses spend the budget of their address, so a stranger blocks neither the account's sign-in from
    /// elsewhere nor a password proof inside its session; only the account-wide ceiling bounds a guess spread over many addresses.</summary>
    [Fact]
    public void Anonymous_guesses_block_their_address_not_the_account()
    {
        var throttle = new BuiltinLoginThrottle();
        for (var attempt = 0; attempt < BuiltinLoginThrottle.FailureLimit; attempt++)
        {
            using var guess = throttle.TryBegin("admin", "203.0.113.1", out _);
            guess!.Fail();
        }
        Assert.Null(throttle.TryBegin("admin", "203.0.113.1", out _));
        using (var elsewhere = throttle.TryBegin("ADMIN", "198.51.100.2", out _))
        using (var signedIn = throttle.TryBegin("admin", out _))
        {
            Assert.NotNull(elsewhere);
            Assert.NotNull(signedIn);
        }
        for (var address = 2; address <= BuiltinLoginThrottle.AccountFailureLimit / BuiltinLoginThrottle.FailureLimit; address++)
        {
            for (var attempt = 0; attempt < BuiltinLoginThrottle.FailureLimit; attempt++)
            {
                using var guess = throttle.TryBegin("admin", $"203.0.113.{address}", out _);
                guess!.Fail();
            }
        }
        Assert.Null(throttle.TryBegin("admin", "198.51.100.2", out _));
        using var proof = throttle.TryBegin("admin", out _);
        Assert.NotNull(proof);
    }

    /// <summary>Owner decision 28. 9.: only failed password proofs spend the budget, so a series of confirmed security changes is
    /// never throttled; running attempts hold their place, so concurrent guesses cannot overshoot; the window starts over.</summary>
    [Fact]
    public void Only_failed_password_proofs_count_and_running_attempts_hold_their_place()
    {
        var clock = new ManualClock();
        var throttle = new BuiltinLoginThrottle(clock);
        for (var success = 0; success < 3 * BuiltinLoginThrottle.FailureLimit; success++)
        {
            using var proof = throttle.TryBegin("admin", out _);
            Assert.NotNull(proof);
        }
        var running = Enumerable.Range(0, BuiltinLoginThrottle.FailureLimit).Select(index => throttle.TryBegin("admin", out _)).ToArray();
        Assert.All(running, Assert.NotNull);
        Assert.Null(throttle.TryBegin("admin", out _));
        foreach (var attempt in running.Skip(1))
        {
            attempt!.Dispose();
        }
        running[0]!.Fail();
        running[0]!.Dispose();
        for (var guess = 1; guess < BuiltinLoginThrottle.FailureLimit; guess++)
        {
            using var wrong = throttle.TryBegin("admin", out _);
            wrong!.Fail();
        }
        Assert.Null(throttle.TryBegin("admin", out _));
        clock.Now += BuiltinLoginThrottle.Window;
        using var later = throttle.TryBegin("admin", out _);
        Assert.NotNull(later);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_login_command_never_formats_the_password_for_logs()
    {
        var command = new LoginRequest("alice", "sensitive-test-password");
        Assert.DoesNotContain(command.Password, command.ToString());
        Assert.DoesNotContain(command.Username, command.ToString());
    }

    [Fact]
    public void Session_renewal_cannot_turn_an_old_login_into_fresh_authentication()
    {
        var now = DateTimeOffset.UtcNow;
        var identity = SignInPrincipal.Create(new UserAccount { Id = Guid.NewGuid(), Username = "alice" }, "oidc:hub", now.AddMinutes(-8));
        Assert.False(SignInPrincipal.HasRecentAuthentication(identity, now));
        var fresh = SignInPrincipal.Create(new UserAccount { Id = Guid.NewGuid(), Username = "alice" }, "oidc:hub", now);
        Assert.True(SignInPrincipal.HasRecentAuthentication(fresh, now));
        Assert.False(SignInPrincipal.HasRecentAuthentication(fresh, now.AddMinutes(-1)));
    }
}
