namespace Atlas.Server.Features.Access.Domain;

/// <summary>Czech counts for the short notes of entries: "1 heslo", "3 hesla", "14 hesel".</summary>
public static class AccessNotes
{
    public static string Count(int count, string one, string few, string many)
    {
        var word = count == 1 ? one : count is >= 2 and <= 4 ? few : many;
        return $"{count} {word}";
    }
}
