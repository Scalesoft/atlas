using Atlas.Server.Features.Vaults.Contracts;

namespace Atlas.Server.Features.Vaults.Queries;

/// <summary>Authorizes a location test and resolves its storage options (the stored password when none is entered).</summary>
public sealed record PrepareLocationTest(TestVaultLocationRequest Request);
