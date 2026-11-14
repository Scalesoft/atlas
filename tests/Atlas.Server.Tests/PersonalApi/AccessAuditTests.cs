using Atlas.Server.Features.AccessAudit.Contracts;
using Atlas.Server.Features.AccessAudit.Queries;
using Xunit;

namespace Atlas.Server.Tests.PersonalApi;

public sealed class AccessAuditTests
{
    [PostgreSqlFact]
    public async Task Level_change_is_audited_as_changed()
    {
        await using var f = await PersonalApiFixture.CreateAsync();
        await f.SetLevel("write");
        await f.SetLevel("read");
        await f.SetLevel("none");
        var audit = await f.As<AccessAuditList>(f.Admin, new GetAccessAudit(null, 50, UserId: f.Owner.Id));
        var events = audit.Items.Where(x => x.Action.StartsWith("api_permission.", StringComparison.Ordinal)).Reverse().ToArray();
        Assert.Equal(["api_permission.enabled", "api_permission.changed", "api_permission.disabled"], events.Select(x => x.Action));
        Assert.Equal(["write", "read", "none"], events.Select(x => x.ApiLevel));
        Assert.Equal(new bool?[] { true, true, false }, events.Select(x => x.CanIssueTokens));
        Assert.All(events, x => Assert.Equal(f.Admin.Id, x.ActorId));
    }
}
