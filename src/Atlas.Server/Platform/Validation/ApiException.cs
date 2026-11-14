using Atlas.Server.Platform.Localization;

namespace Atlas.Server.Platform.Validation;

/// <summary>An error the API returns: status code, user-facing Czech message, an optional machine-readable code and optional
/// extension members of the problem body (e.g. the protection mode of a refused release). The problem title is the message in
/// the request's language (<see cref="ErrorTexts"/>, keyed by the Czech text). With <c>args</c> the message is a template:
/// <c>new ApiException(404, "Prostor {name} nebyl nalezen.", args: new Dictionary&lt;string, object?&gt; { ["name"] = name })</c>.
/// <see cref="Exception.Message"/> is the filled Czech text (logs, MCP).</summary>
public sealed class ApiException : Exception
{
    public ApiException(int status, string message, string? code = null, IReadOnlyDictionary<string, object?>? extensions = null,
        IReadOnlyDictionary<string, object?>? args = null) : base(args is null ? message : ErrorTexts.Format(message, args))
    {
        Status = status;
        Code = code;
        Extensions = extensions;
        Template = message;
        Args = args;
    }

    /// <summary>The Czech text before its values are filled in: the key of its translation.</summary>
    public string Template { get; }

    /// <summary>The values of the template's placeholders, in every language.</summary>
    public IReadOnlyDictionary<string, object?>? Args { get; }

    public int Status { get; }

    public string? Code { get; }

    public IReadOnlyDictionary<string, object?>? Extensions { get; }
}
