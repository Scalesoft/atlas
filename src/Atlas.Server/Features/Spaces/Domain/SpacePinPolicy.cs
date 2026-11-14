namespace Atlas.Server.Features.Spaces.Domain;

/// <summary>Who has a space pinned in the sidebar. None: whoever pins it; Default: everyone until they unpin it; Required: everyone,
/// and nobody can unpin it. Stored and sent by name.</summary>
public enum SpacePinPolicy
{
    None,
    Default,
    Required
}
