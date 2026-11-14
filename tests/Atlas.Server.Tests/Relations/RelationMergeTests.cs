using Atlas.Server.Features.Relations.Queries;
using Atlas.Server.Platform.Resources;
using Xunit;

namespace Atlas.Server.Tests.Relations;

public sealed class RelationMergeTests
{
    [Fact]
    public void Depth_two_merges_the_uses_of_one_pair_and_kind_and_keeps_drafts_and_other_kinds_apart()
    {
        var (page, catalog) = (Guid.NewGuid(), Guid.NewGuid());
        var merged = GetObjectRelationsHandler.Merged([
            new ObjectEdge(page, catalog, RelationKinds.Catalog, 1, "db.version"),
            new ObjectEdge(page, catalog, RelationKinds.Catalog, 2, "portal.ssl"),
            new ObjectEdge(page, catalog, RelationKinds.Catalog, 1, "portal.sso", Draft: true),
            new ObjectEdge(page, catalog, RelationKinds.Link)]).ToArray();

        Assert.Equal(3, merged.Length);
        Assert.Contains(new ObjectEdge(page, catalog, RelationKinds.Catalog, 3), merged);
        Assert.Contains(new ObjectEdge(page, catalog, RelationKinds.Catalog, 1, "portal.sso", Draft: true), merged);
        Assert.Contains(new ObjectEdge(page, catalog, RelationKinds.Link), merged);
    }
}
