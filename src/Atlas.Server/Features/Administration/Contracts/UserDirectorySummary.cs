namespace Atlas.Server.Features.Administration.Contracts;

/// <summary>The numbers of the account directory, counted in the database: pending = active with a password not yet set, admins = wiki administrators.</summary>
public sealed record UserDirectorySummary(int Total, int Pending, int Deactivated, int Admins);
