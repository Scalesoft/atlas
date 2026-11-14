using Xunit;

namespace Atlas.Server.Tests;

public sealed class PostgreSqlFactAttributeTests
{
    [Fact]
    public void A_missing_connection_skips_locally_and_fails_on_CI()
    {
        Assert.Null(PostgreSqlFactAttribute.SkipReason(null, "Host=db"));
        Assert.Null(PostgreSqlFactAttribute.SkipReason("true", "Host=db"));
        Assert.Contains("ATLAS_TEST_CONNECTION", PostgreSqlFactAttribute.SkipReason(null, " "));
        Assert.Throws<InvalidOperationException>(() => PostgreSqlFactAttribute.SkipReason("true", null));
        Assert.Throws<InvalidOperationException>(() => PostgreSqlFactAttribute.SkipReason("true", ""));
    }
}
