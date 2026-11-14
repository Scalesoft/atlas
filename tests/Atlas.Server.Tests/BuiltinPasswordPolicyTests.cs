using Xunit;
using Atlas.Server.Features.Auth.Builtin;
namespace Atlas.Server.Tests;
public sealed class BuiltinPasswordPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Short_test_password_needs_both_development_and_explicit_opt_in(bool development, bool optIn)
        => Assert.Throws<ApiException>(() => new BuiltinPasswordPolicy(development, optIn).Validate("test"));
    [Fact]
    public void Explicit_local_development_accepts_test_password_but_still_rejects_empty()
    {
        var policy = new BuiltinPasswordPolicy(true, true);
        policy.Validate("test");
        Assert.Throws<ApiException>(() => policy.Validate(""));
    }
}
