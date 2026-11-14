using System.Text.Json;
using System.Text.Json.Nodes;
using Atlas.Markdown.Sync.Recognition;
using Atlas.Server.Features.Pages;
using Atlas.Server.Features.Pages.Commands;
using Atlas.Server.Features.Pages.Contracts;
using Atlas.Server.Features.Pages.Domain;
using Atlas.Server.Platform.Content;
using NHibernate;
using ISession = NHibernate.ISession;

namespace Atlas.Server.Platform.Linking;

/// <summary><c>link: "auto"</c> (default) or <c>"off"</c> of a Markdown write.</summary>
public static class LinkModes
{
    public static bool Auto(string? link) => link switch
    {
        null or "" or "auto" => true,
        "off" => false,
        _ => throw new ApiException(400, "link je \"auto\" (výchozí), nebo \"off\".", "invalid_argument"),
    };
}

/// <summary>What the token channels add: a space switched out of AI and MCP is missing to them (G5, D10), as to MCP.</summary>
public static class LinkingScope
{
    public static async Task RequireAsync(ISession session, IRequestChannel channel, Guid spaceId, string missing, CancellationToken ct)
    {
        var current = channel.Current;
        if (current.ApiTokenId is null && !current.IsAiBound)
        {
            return;
        }
        var included = await session.CreateSQLQuery($"SELECT {AccessSql.SpaceInAi(":space")} AS included").AddScalar("included", NHibernateUtil.Boolean)
            .SetParameter("space", spaceId).UniqueResultAsync<bool>(ct);
        if (!included)
        {
            throw new ApiException(404, missing);
        }
    }

    public const string MissingSpace = "Prostor nebyl nalezen.";
    public const string MissingPage = "Stránka nebyla nalezena.";
}
