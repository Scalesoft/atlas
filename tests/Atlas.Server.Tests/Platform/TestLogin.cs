namespace Atlas.Server.Tests.Platform;

/// <summary>A login for a test's user on the shared host and database, where "writer" is not the only one: the readable name with
/// a unique suffix. The display name stays what the test says.</summary>
internal static class TestLogin
{
    public static string For(string name) => name + "-" + Guid.NewGuid().ToString("N")[..8];
}
