namespace Atlas.Server.Platform.Validation;

/// <summary>Space access levels; the numeric values are persisted in SpaceGrant.Role.</summary>
public enum SpaceRole
{
    Reader = 1,
    Writer = 2,
    Admin = 3
}
