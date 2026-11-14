namespace Atlas.Server.Features.Search.Contracts;

public sealed record SearchWidget(string Kind, string Title, string Detail, string? HeadingId)
{
    public string? Key { get; init; }
    public string? State { get; init; }
    public string? Url { get; init; }

    /// <summary>A contact hit's contact as the page's contact block holds it (the editor's contact fields only), from the same
    /// revision the hit was authorized for, so a picker takes it over instead of parsing <see cref="Detail"/>.</summary>
    public System.Text.Json.Nodes.JsonObject? Contact { get; init; }
}
