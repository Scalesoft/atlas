using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>A write through an NHibernate session that IntegrityRules refuses (C# since 3.6; the guard triggers refused native SQL
/// before, which IntegrityRulesConventionTests now keeps out of the server).</summary>
internal static class IntegrityRefusal
{
    public static async Task AssertAsync(AtlasTestHost host, string message, Func<NHibernate.ISession, Task> change)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.SeedAsync(async session =>
        {
            await change(session);
            await session.FlushAsync();
        }));
        Assert.Equal(message, error.Message);
    }
}
