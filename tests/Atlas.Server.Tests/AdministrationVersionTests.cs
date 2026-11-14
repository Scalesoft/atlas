using Xunit;
namespace Atlas.Server.Tests;
public class AdministrationVersionTests
{
    [Fact] public void Exact_current_UTC_token_matches()
    {
        var current=new DateTime(2026,9,6,12,0,0,DateTimeKind.Utc);
        AdministrationVersion.Expect(current,AdministrationVersion.Format(current));
    }
    [Theory]
    [InlineData("2026-09-06T12:00:00+00:00")]
    [InlineData("2026-09-06T14:00:00+02:00")]
    public void Version_is_an_opaque_token_not_a_reinterpreted_timestamp(string alternative)
    {
        var current=new DateTime(2026,9,6,12,0,0,DateTimeKind.Utc);
        Assert.Equal(409,Assert.Throws<ApiException>(()=>AdministrationVersion.Expect(current,alternative)).Status);
    }
    [Fact] public void Missing_token_cannot_overwrite_existing_relation() => Assert.Throws<ApiException>(()=>AdministrationVersion.Expect(DateTime.UtcNow,null));
    [Fact] public void Stale_token_cannot_recreate_removed_relation() => Assert.Throws<ApiException>(()=>AdministrationVersion.Expect(null,AdministrationVersion.Format(DateTime.UtcNow)));
}
