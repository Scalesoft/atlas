namespace Atlas.Server.Features.Administration.Contracts;

public sealed record ProfileSecurity(bool HasBuiltinCredential, string CredentialState, string? Username, string? Email);
