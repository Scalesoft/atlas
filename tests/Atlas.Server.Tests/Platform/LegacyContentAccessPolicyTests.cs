using Xunit;

namespace Atlas.Server.Tests.Platform;

public sealed class LegacyContentAccessPolicyTests
{
    [Theory]
    [InlineData(false, 1, 1, true)]
    [InlineData(false, 1, 2, false)]
    [InlineData(false, 1, 3, false)]
    [InlineData(false, 2, 1, true)]
    [InlineData(false, 2, 2, true)]
    [InlineData(false, 2, 3, false)]
    [InlineData(false, 3, 1, true)]
    [InlineData(false, 3, 2, true)]
    [InlineData(false, 3, 3, true)]
    [InlineData(false, null, 1, false)]
    [InlineData(true, null, 1, true)]
    [InlineData(true, null, 2, true)]
    [InlineData(true, null, 3, true)]
    [InlineData(false, 999, 1, false)]
    [InlineData(false, 999, 3, false)]
    [InlineData(false, 3, 0, false)]
    [InlineData(false, 3, -1, false)]
    [InlineData(true, null, 999, false)]
    [InlineData(true, 3, 0, false)]
    public void Existing_roles_keep_their_capabilities_but_unknown_values_never_grant_access(
        bool wikiAdmin, int? grant, int required, bool expected)
    {
        Assert.Equal(expected, ResourceAccessPolicy.AllowsLegacyContent(wikiAdmin, grant is { } value ? (SpaceRole)value : null, (SpaceRole)required));
    }
}
