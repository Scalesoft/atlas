using System.Security.Cryptography;
using Atlas.Server.Features.PersonalApi.Domain;

namespace Atlas.Server.Tests.Platform;

/// <summary>Personal API token rows for handler tests that run a message as the api or mcp channel. Only the metadata a revision,
/// publication or opening refers to: the secret is never used, authentication is not under test there.</summary>
internal static class TestApiTokens
{
    public static async Task<Guid> SeedAsync(AtlasTestHost host, UserAccount owner, string name)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await host.SeedAsync(session => session.SaveAsync(new PersonalApiToken
        {
            Id = id, UserId = owner.Id, Name = name, TokenHash = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Prefix = "atl_test", SecurityStamp = Guid.NewGuid().ToString("N"), ActiveFrom = now, ExpiresAt = now.AddDays(30)
        }));
        return id;
    }

    public static Task RevokeAsync(AtlasTestHost host, Guid tokenId)
        => host.ExecuteAsync("UPDATE \"PersonalApiToken\" SET \"ActiveTo\"=clock_timestamp() WHERE \"Id\"=:id", tokenId);
}
