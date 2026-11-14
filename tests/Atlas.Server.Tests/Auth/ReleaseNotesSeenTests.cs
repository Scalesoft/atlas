using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Auth;

public sealed class ReleaseNotesSeenTests
{
    /// <summary>„Co je nového“: a new account has seen nothing (the shell records the running release without the notice), a seen
    /// release is the account's, not the device's, and only a released version is recorded.</summary>
    [PostgreSqlFact]
    public async Task The_seen_release_is_kept_per_account_and_only_a_release_is_accepted()
    {
        await using var host = await AtlasTestHost.CreateAsync("atlas_release_notes_");
        var user = await host.SeedUserAsync("reader");
        var other = await host.SeedUserAsync("other");
        host.Actor.Current = user;
        Assert.Null((await host.InvokeAsync<UserDto>(new GetCurrentUser())).ReleaseNotesSeen);

        await host.InvokeAsync(new MarkReleaseNotesSeen("3.5.0"));
        Assert.Equal("3.5.0", (await host.InvokeAsync<UserDto>(new GetCurrentUser())).ReleaseNotesSeen);
        await host.InvokeAsync(new MarkReleaseNotesSeen("3.5.1"));
        Assert.Equal("3.5.1", (await host.InvokeAsync<UserDto>(new GetCurrentUser())).ReleaseNotesSeen);

        foreach (var version in new[] { "3.6.0-dev", "3.6.0+abc", "", "3.6.0\n", null! })
        {
            var refused = await Assert.ThrowsAsync<ApiException>(() => host.InvokeAsync(new MarkReleaseNotesSeen(version)));
            Assert.Equal(400, refused.Status);
        }
        Assert.Equal("3.5.1", (await host.InvokeAsync<UserDto>(new GetCurrentUser())).ReleaseNotesSeen);

        host.Actor.Current = other;
        Assert.Null((await host.InvokeAsync<UserDto>(new GetCurrentUser())).ReleaseNotesSeen);
    }
}
