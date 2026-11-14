using Atlas.Server.Platform.Localization;

namespace Atlas.Server.Platform.Hosting;

/// <summary>
/// The one problem+json body the API answers errors with. The error middleware and the cookie
/// handler both write through here so a 401 looks the same whoever produced it. The title is
/// translated here, into the request's language (<see cref="ErrorTexts"/>); the code is not.
/// </summary>
public static class ProblemResponse
{
    /// <summary>
    /// The longest title a problem (or an MCP tool error) carries: a message that lists what the request named (unknown component
    /// numbers, say) never grows with the request.
    /// </summary>
    public const int MaxTitleLength = 2000;

    public static string Title(string title) => title.Length <= MaxTitleLength ? title : title[..(MaxTitleLength - 1)] + "…";

    /// <summary>members: further extension members of the body (ApiException.Extensions), next to code. title: the Czech text,
    /// with <paramref name="args"/> a template (ApiException.Template and Args).</summary>
    public static Task WriteAsync(HttpContext context, int status, string title, string? code = null,
        IReadOnlyDictionary<string, object?>? members = null, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return Task.CompletedTask;
        }
        Dictionary<string, object?>? extensions = code is null && members is null ? null : new(members ?? new Dictionary<string, object?>());
        if (code is not null)
        {
            extensions!["code"] = code;
        }
        return Results.Problem(statusCode: status, title: Title(ErrorTexts.Translate(RequestLanguage.Of(context), title, args)), extensions: extensions)
            .ExecuteAsync(context);
    }
}
