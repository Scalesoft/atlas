using Atlas.Server.Features.PageAccess;
using Atlas.Server.Features.PageAccess.Queries;
using Atlas.Server.Features.Pages.Queries;
using Atlas.Server.Tests.ExternalSharing;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.PageAccess;

public sealed class PageAccessHistoryTests
{
    [PostgreSqlFact]
    public async Task Opening_through_the_api_records_channel_and_token()
    {
        await using var fixture = await SharingFixture.CreateAsync();
        var tokenId = await TestApiTokens.SeedAsync(fixture.Host, fixture.Reader, "Reader script");
        fixture.Host.Actor.Current = fixture.Reader;
        fixture.Host.Channel.Current = new RequestChannel(ContentChannel.Api, tokenId);
        await fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId));
        fixture.Host.Channel.Current = RequestChannel.Web;
        await fixture.Host.InvokeAsync<PageDto>(new GetPage(fixture.PageId));

        // A wiki admin sees the token by name; a space Admin who manages the page but does not own the token sees only the channel.
        fixture.Host.Actor.Current = fixture.WikiAdmin;
        var items = (await fixture.Host.InvokeAsync<PageAccessHistory>(new GetPageAccessHistory(fixture.PageId))).Items;
        Assert.Equal(new[] { ("web", (Guid?)null), ("api", tokenId) }, items.Select(x => (x.Channel!, x.ApiToken?.Id)));
        Assert.Equal("Reader script", items[1].ApiToken!.Name);
        fixture.Host.Actor.Current = fixture.Admin;
        var managed = (await fixture.Host.InvokeAsync<PageAccessHistory>(new GetPageAccessHistory(fixture.PageId))).Items;
        Assert.Equal(new[] { ("web", (ApiTokenRef?)null), ("api", (ApiTokenRef?)null) }, managed.Select(x => (x.Channel!, x.ApiToken)));
    }
}
