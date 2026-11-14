namespace Atlas.Server.Features.Collaboration.Domain;

public readonly record struct WikiDocumentId(Guid PageId, Guid Generation)
{
    public override string ToString() => $"page:{PageId:N}:{Generation:N}";
    public static WikiDocumentId Parse(string value)
    {
        var parts = value?.Split(':');
        if (parts is not { Length: 3 } || parts[0] != "page"
            || !Guid.TryParseExact(parts[1], "N", out var page) || page == Guid.Empty
            || !Guid.TryParseExact(parts[2], "N", out var generation) || generation == Guid.Empty)
            throw new FormatException("Invalid wiki collaboration document identifier.");
        var id = new WikiDocumentId(page, generation);
        if (id.ToString() != value)
        {
            throw new FormatException("Noncanonical wiki collaboration document identifier.");
        }
        return id;
    }
}
