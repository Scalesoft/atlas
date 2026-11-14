namespace Atlas.Server.Features.Mcp.Domain;

/// <summary>
/// Ids of headings a draft tool creates (D8: a new heading gets a UUID, as the editor's crypto.randomUUID()). A service rather than
/// an inline Guid.NewGuid() so tests can see when the conversion runs, between the read and the write transaction.
/// </summary>
public class McpHeadingIds
{
    public virtual string Next() => Guid.NewGuid().ToString();
}
