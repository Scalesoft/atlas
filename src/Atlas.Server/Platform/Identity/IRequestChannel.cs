namespace Atlas.Server.Platform.Identity;

/// <summary>The transport a change or an opening came through. Never the kind of operation: a restore or a source edit
/// is web or api like any other write. Import is set explicitly by the Wiki.js import and the backup restore. Assistant is the
/// in-process assistant acting as the signed-in user: no token, and its ceiling is the user's web content rights (spec D19).</summary>
public enum ContentChannel
{
    Web,
    Api,
    Mcp,
    Assistant,
    Import,
    System
}

/// <summary>The channel of the current unit of work and, for api and mcp, the personal API token it authenticated with.
/// The token is a fact next to the author, never instead of it: the author is always the token owner.</summary>
public sealed record RequestChannel
{
    public static readonly RequestChannel Web = new(ContentChannel.Web, null);
    public static readonly RequestChannel Assistant = new(ContentChannel.Assistant, null);
    public static readonly RequestChannel Import = new(ContentChannel.Import, null);
    public static readonly RequestChannel System = new(ContentChannel.System, null);

    public RequestChannel(ContentChannel channel, Guid? apiTokenId)
    {
        if ((channel is ContentChannel.Api or ContentChannel.Mcp) != (apiTokenId is not null))
        {
            throw new ArgumentException("A token belongs to the api and mcp channels, and only to them.", nameof(apiTokenId));
        }
        Channel = channel;
        ApiTokenId = apiTokenId;
    }

    public ContentChannel Channel { get; }
    public Guid? ApiTokenId { get; }

    /// <summary>The assistant conversation of a confirmed proposal (channel assistant only), for the live update log (3.6-H).</summary>
    public Guid? ConversationId { get; init; }

    /// <summary>The stored value of the "Channel" columns.</summary>
    public string Code => ContentChannels.Code(Channel);

    public bool IsMcp => Channel == ContentChannel.Mcp;

    /// <summary>The content may go to a model: over MCP, to the in-process assistant, or over /api/v1, whose personal tokens AI tools use
    /// too. G5 hides a space excluded from AI and MCP (D10). Every G5 decision asks this, never <see cref="IsMcp"/>, which only says a token
    /// came over /mcp; the web session is never AI-bound.</summary>
    public bool IsAiBound => Channel is ContentChannel.Mcp or ContentChannel.Assistant or ContentChannel.Api;

    /// <summary>An agent writes without seeing the editor, so it refuses unsaved live edits of anyone, its own user's included (S5).</summary>
    public bool RefusesOwnUnsaved => Channel is ContentChannel.Mcp or ContentChannel.Assistant;
}

/// <summary>Stored channel values. "legacy" marks rows from before Atlas 2.0 and is never written; reads answer it as null.</summary>
public static class ContentChannels
{
    public const string Web = "web";
    public const string Api = "api";
    public const string Mcp = "mcp";
    public const string Assistant = "assistant";
    public const string Import = "import";
    public const string System = "system";
    public const string Legacy = "legacy";

    public static string Code(ContentChannel channel) => channel switch
    {
        ContentChannel.Web => Web,
        ContentChannel.Api => Api,
        ContentChannel.Mcp => Mcp,
        ContentChannel.Assistant => Assistant,
        ContentChannel.Import => Import,
        ContentChannel.System => System,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null)
    };

    /// <summary>What a read model answers: the stored value, or null for legacy rows and rows without a channel.</summary>
    public static string? ForRead(string? stored) => stored is null or Legacy ? null : stored;
}

/// <summary>Decided by the server, never by the client: cookie → web, or assistant inside the assistant's tool session; the personal
/// API scheme on /api/v1 → api and on /mcp → mcp; no HttpContext → system.</summary>
public interface IRequestChannel
{
    RequestChannel Current { get; }
}
