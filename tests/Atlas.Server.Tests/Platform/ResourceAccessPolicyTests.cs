using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>The document policy. Vaults and secrets are decided by VaultGrantSnapshot (VaultAccessTests).</summary>
public sealed class ResourceAccessPolicyTests
{
    private static readonly Guid User = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Group = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid Link = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly Guid Space = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid Document = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static ResourceAccessPrincipal Actor => new(AccessContextKind.Internal, User, true, false, new HashSet<Guid> { Group });
    private static ResourceAccessTarget Page => new(Space, AccessResourceKind.Document, Document);

    private static ResourceAccessGrant Grant(AccessRole role = AccessRole.DocumentReader) =>
        new(Guid.NewGuid(), AccessSubjectKind.User, User, Space, AccessScopeKind.Resource,
            AccessResourceKind.Document, Document, role, Now.AddHours(-1));

    private static ResourceAccessGrant Defaults(AccessRole role) =>
        Grant(role) with { Scope = AccessScopeKind.SpaceDefaults, ResourceId = null };

    private static ResourceAccessDecision Evaluate(ResourceAccessTarget target, AccessOperation operation,
        params ResourceAccessGrant[] grants) => ResourceAccessPolicy.Evaluate(Actor, target, operation, grants, Now);

    [Fact]
    public void Template_controls_require_space_administrator_grant_even_with_direct_manager()
    {
        var template = Page with { IsTemplate = true };
        var direct = Grant(AccessRole.DocumentManager);
        var writer = Defaults(AccessRole.DocumentWriter);
        var admin = Defaults(AccessRole.DocumentManager);
        Assert.True(Evaluate(template, AccessOperation.ReadContent, writer).Allowed);
        foreach (var operation in new[] { AccessOperation.EditContent, AccessOperation.ManageAccess, AccessOperation.Share })
        {
            Assert.False(Evaluate(template, operation, writer, direct).Allowed);
            Assert.Equal([admin.Id], Evaluate(template, operation, writer, direct, admin).GrantIds);
        }
    }

    [Theory]
    [InlineData(AccessRole.DocumentReader, AccessOperation.Discover, true)]
    [InlineData(AccessRole.DocumentReader, AccessOperation.ReadContent, true)]
    [InlineData(AccessRole.DocumentReader, AccessOperation.EditContent, false)]
    [InlineData(AccessRole.DocumentReader, AccessOperation.Share, false)]
    [InlineData(AccessRole.DocumentReader, AccessOperation.ManageAccess, false)]
    [InlineData(AccessRole.DocumentWriter, AccessOperation.ReadContent, true)]
    [InlineData(AccessRole.DocumentWriter, AccessOperation.EditContent, true)]
    [InlineData(AccessRole.DocumentWriter, AccessOperation.ManageAccess, false)]
    [InlineData(AccessRole.DocumentWriter, AccessOperation.Share, false)]
    [InlineData(AccessRole.DocumentManager, AccessOperation.ManageAccess, true)]
    [InlineData(AccessRole.DocumentManager, AccessOperation.Share, true)]
    [InlineData((AccessRole)999, AccessOperation.Discover, false)]
    [InlineData(AccessRole.DocumentManager, (AccessOperation)999, false)]
    public void Document_roles_are_explicit(AccessRole role, AccessOperation operation, bool expected)
    {
        var grant = Grant(role);
        var decision = Evaluate(Page, operation, grant);
        Assert.Equal(expected, decision.Allowed);
        Assert.Equal(expected ? new[] { grant.Id } : [], decision.GrantIds);
    }

    [Fact]
    public void No_grant_means_no_access()
    {
        foreach (var operation in Enum.GetValues<AccessOperation>())
            Assert.False(Evaluate(Page, operation).Allowed);
    }

    [Fact]
    public void Space_defaults_are_scoped_to_their_space_and_type_and_interrupted_by_own_access()
    {
        var grant = Defaults(AccessRole.DocumentReader);
        Assert.True(Evaluate(Page, AccessOperation.Discover, grant).Allowed);
        Assert.False(Evaluate(Page with { OwnAccess = true }, AccessOperation.Discover, grant).Allowed);
        Assert.False(Evaluate(Page with { SpaceId = Other }, AccessOperation.Discover, grant).Allowed);
        Assert.False(Evaluate(Page with { Kind = (AccessResourceKind)999 }, AccessOperation.Discover, grant).Allowed);
        Assert.False(Evaluate(Page, AccessOperation.Discover, grant with { ResourceKind = (AccessResourceKind)999 }).Allowed);
    }

    [Theory]
    [InlineData(AccessOperation.Discover)]
    [InlineData(AccessOperation.ReadContent)]
    [InlineData(AccessOperation.EditContent)]
    [InlineData(AccessOperation.ManageAccess)]
    [InlineData(AccessOperation.Share)]
    public void Space_default_document_manager_reaches_own_access_pages(AccessOperation operation)
    {
        var manager = Defaults(AccessRole.DocumentManager);
        var decision = Evaluate(Page with { OwnAccess = true }, operation, manager);
        Assert.True(decision.Allowed);
        Assert.Equal(new[] { manager.Id }, decision.GrantIds);
        Assert.False(Evaluate(Page with { OwnAccess = true, SpaceId = Other }, operation, manager).Allowed);
        Assert.False(Evaluate(Page with { OwnAccess = true }, operation, manager with { ExpiresAt = Now }).Allowed);
    }

    [Theory]
    [InlineData(AccessRole.DocumentReader, AccessOperation.ReadContent)]
    [InlineData(AccessRole.DocumentWriter, AccessOperation.ReadContent)]
    [InlineData(AccessRole.DocumentWriter, AccessOperation.EditContent)]
    public void Own_access_page_ignores_space_defaults_below_manager(AccessRole role, AccessOperation operation)
    {
        var defaults = Defaults(role);
        Assert.True(Evaluate(Page, operation, defaults).Allowed);
        Assert.False(Evaluate(Page with { OwnAccess = true }, operation, defaults).Allowed);
    }

    [Fact]
    public void Direct_page_grant_adds_to_space_defaults_only_on_its_own_page()
    {
        var defaults = Defaults(AccessRole.DocumentReader);
        var direct = Grant(AccessRole.DocumentWriter);
        var sibling = Page with { Id = Other };
        var edit = Evaluate(Page, AccessOperation.EditContent, defaults, direct);
        Assert.True(edit.Allowed);
        Assert.Equal(new[] { direct.Id }, edit.GrantIds);
        Assert.Equal(new[] { defaults.Id, direct.Id }, Evaluate(Page, AccessOperation.ReadContent, defaults, direct).GrantIds);
        Assert.True(Evaluate(sibling, AccessOperation.ReadContent, defaults, direct).Allowed);
        Assert.False(Evaluate(sibling, AccessOperation.EditContent, defaults, direct).Allowed);
        Assert.False(Evaluate(sibling with { OwnAccess = true }, AccessOperation.Discover, defaults, direct).Allowed);
        Assert.True(Evaluate(Page with { OwnAccess = true }, AccessOperation.EditContent, defaults, direct).Allowed);
        Assert.False(Evaluate(Page with { OwnAccess = true }, AccessOperation.ManageAccess, defaults, direct).Allowed);
    }

    [Fact]
    public void Current_direct_and_group_grants_union_but_inapplicable_grants_are_not_explanations()
    {
        var direct = Grant(AccessRole.DocumentReader);
        var group = Grant(AccessRole.DocumentWriter) with { SubjectKind = AccessSubjectKind.Group, SubjectId = Group };
        var another = group with { Id = Guid.NewGuid(), SubjectId = Other };
        var decision = Evaluate(Page, AccessOperation.EditContent, direct, group, another, group);
        Assert.True(decision.Allowed);
        Assert.Equal(new[] { group.Id }, decision.GrantIds);
        var removedMembership = Actor with { GroupIds = new HashSet<Guid>() };
        Assert.False(ResourceAccessPolicy.Evaluate(removedMembership, Page, AccessOperation.EditContent, [direct, group], Now).Allowed);
    }

    [Theory]
    [InlineData(-1, 1, false, true)]
    [InlineData(0, 1, false, true)]
    [InlineData(1, 2, false, false)]
    [InlineData(-2, 0, false, false)]
    [InlineData(-2, -1, false, false)]
    [InlineData(-1, 1, true, false)]
    [InlineData(0, 0, false, false)]
    [InlineData(1, -1, false, false)]
    public void Validity_is_inclusive_at_start_exclusive_at_expiry_and_revocation_wins(int startTicks, int expiryTicks, bool revoked, bool expected)
    {
        Assert.Equal(expected, Evaluate(Page, AccessOperation.ReadContent,
            Grant() with { StartsAt = Now.AddTicks(startTicks), ExpiresAt = Now.AddTicks(expiryTicks), Revoked = revoked }).Allowed);
    }

    [Fact]
    public void Expired_writer_and_current_reader_cannot_splice_privilege_and_validity()
    {
        Assert.False(Evaluate(Page, AccessOperation.EditContent,
            Grant(AccessRole.DocumentWriter) with { ExpiresAt = Now },
            Grant(AccessRole.DocumentReader)).Allowed);
    }

    [Fact]
    public void Expiry_compares_instants_not_local_clock_components()
    {
        Assert.False(Evaluate(Page, AccessOperation.ReadContent, Grant() with { ExpiresAt = Now.ToOffset(TimeSpan.FromHours(2)) }).Allowed);
        Assert.True(Evaluate(Page, AccessOperation.ReadContent, Grant() with { ExpiresAt = Now.AddTicks(1).ToOffset(TimeSpan.FromHours(-5)) }).Allowed);
    }

    [Fact]
    public void Inactive_account_cannot_use_grants_or_administrator_bypass()
    {
        var inactive = Actor with { IsActive = false, IsWikiAdmin = true };
        Assert.False(ResourceAccessPolicy.Evaluate(inactive, Page, AccessOperation.ReadContent, [Grant()], Now).Allowed);
        Assert.False(ResourceAccessPolicy.Evaluate(inactive, Page, AccessOperation.ReadContent, [], Now).Allowed);
    }

    [Fact]
    public void Revoking_one_of_two_qualifying_grants_preserves_other_access_and_explanation()
    {
        var direct = Grant();
        var group = Grant() with { SubjectKind = AccessSubjectKind.Group, SubjectId = Group };
        var before = Evaluate(Page, AccessOperation.ReadContent, direct, group);
        Assert.True(before.Allowed);
        Assert.Equal(new[] { direct.Id, group.Id }, before.GrantIds);
        var after = Evaluate(Page, AccessOperation.ReadContent, direct with { Revoked = true }, group);
        Assert.True(after.Allowed);
        Assert.Equal(new[] { group.Id }, after.GrantIds);
        Assert.False(Evaluate(Page, AccessOperation.ReadContent, direct with { Revoked = true }, group with { Revoked = true }).Allowed);
    }

    [Fact]
    public void Legacy_administrator_keeps_content_administration()
    {
        var admin = Actor with { IsWikiAdmin = true };
        foreach (var operation in Enum.GetValues<AccessOperation>())
        {
            var decision = ResourceAccessPolicy.Evaluate(admin, Page with { OwnAccess = true }, operation, [], Now);
            Assert.True(decision.Allowed);
            Assert.True(decision.LegacyAdministrator);
        }
    }

    [Fact]
    public void Share_context_never_unions_internal_user_group_or_admin_authority()
    {
        var shared = Actor with { Context = AccessContextKind.Shared, SubjectId = Link, IsWikiAdmin = true };
        var userGrant = Grant(AccessRole.DocumentManager) with { SubjectId = Link };
        var groupGrant = userGrant with { SubjectKind = AccessSubjectKind.Group, SubjectId = Group };
        Assert.False(ResourceAccessPolicy.Evaluate(shared, Page, AccessOperation.ReadContent, [userGrant, groupGrant], Now).Allowed);
    }

    [Fact]
    public void Share_only_allows_explicit_current_document_reader_assignment_for_that_link()
    {
        var shared = new ResourceAccessPrincipal(AccessContextKind.Shared, Link, true, false, new HashSet<Guid>());
        var grant = Grant(AccessRole.DocumentReader) with { SubjectKind = AccessSubjectKind.ShareLink, SubjectId = Link };
        ResourceAccessDecision Check(ResourceAccessGrant candidate, ResourceAccessTarget target, AccessOperation operation) =>
            ResourceAccessPolicy.Evaluate(shared, target, operation, [candidate], Now);
        Assert.True(Check(grant, Page, AccessOperation.ReadContent).Allowed);
        Assert.True(Check(grant, Page with { OwnAccess = true }, AccessOperation.ReadContent).Allowed);
        Assert.False(Check(grant, Page with { Id = Other }, AccessOperation.ReadContent).Allowed);
        Assert.False(Check(grant with { SubjectId = Other }, Page, AccessOperation.ReadContent).Allowed);
        Assert.False(Check(grant with { Scope = AccessScopeKind.SpaceDefaults, ResourceId = null }, Page, AccessOperation.ReadContent).Allowed);
        Assert.False(Check(grant with { ExpiresAt = Now }, Page, AccessOperation.ReadContent).Allowed);
        Assert.False(Check(grant with { Revoked = true }, Page, AccessOperation.ReadContent).Allowed);
        Assert.False(Check(grant, Page, AccessOperation.Share).Allowed);
        Assert.False(Check(grant, Page, AccessOperation.EditContent).Allowed);
        Assert.False(Check(grant, Page, AccessOperation.ManageAccess).Allowed);
        Assert.False(Check(grant with { Role = AccessRole.DocumentManager }, Page, AccessOperation.ReadContent).Allowed);
        Assert.False(ResourceAccessPolicy.Evaluate(Actor, Page, AccessOperation.ReadContent, [grant], Now).Allowed);
    }

    [Fact]
    public void Invalid_identifiers_shapes_and_future_values_fail_closed()
    {
        var malformedGrants = new[]
        {
            Grant() with { Id = Guid.Empty }, Grant() with { SubjectId = Guid.Empty },
            Grant() with { ResourceId = null }, Grant() with { ResourceId = Guid.Empty },
            Grant() with { SubjectKind = (AccessSubjectKind)999 },
            Grant() with { Scope = (AccessScopeKind)999 },
            Grant() with { Scope = AccessScopeKind.SpaceDefaults },
            Grant() with { SpaceId = Guid.Empty },
        };
        foreach (var grant in malformedGrants)
            Assert.False(Evaluate(Page, AccessOperation.ReadContent, grant).Allowed);
        foreach (var target in new[] { Page with { SpaceId = Guid.Empty }, Page with { Id = Guid.Empty }, Page with { Kind = (AccessResourceKind)999 } })
            Assert.False(Evaluate(target, AccessOperation.ReadContent, Grant()).Allowed);
        foreach (var principal in new[] { Actor with { SubjectId = Guid.Empty }, Actor with { Context = (AccessContextKind)999 } })
            Assert.False(ResourceAccessPolicy.Evaluate(principal, Page, AccessOperation.ReadContent, [Grant()], Now).Allowed);
    }
}
