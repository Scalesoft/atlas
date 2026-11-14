namespace Atlas.Server.Features.Access.Domain;

/// <summary>Direct: only the grants set on the resource. Effective: also everyone who reaches it through a group or the space.</summary>
public enum AccessView
{
    Effective,
    Direct
}
