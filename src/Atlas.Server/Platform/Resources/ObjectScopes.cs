namespace Atlas.Server.Platform.Resources;

/// <summary>Who an object is for (objects spec §2.3): the whole wiki, one space, or its owner alone.</summary>
public static class ObjectScopes
{
    public const string Wiki = "wiki";
    public const string Space = "space";
    public const string Private = "private";

    public static bool IsKnown(string? scope) => scope is Wiki or Space or Private;
}
