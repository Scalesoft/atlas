using System.Text.Json;
using Atlas.Server.Platform.Localization;
using Atlas.Server.Platform.Validation;
using Microsoft.AspNetCore.Antiforgery;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Providers;

namespace Atlas.Server.Platform.Hosting;

public static class RequestErrorHandling
{
    /// <summary>The API routes of content and its organization, which carry no credentials: their failures are logged in full.</summary>
    private static readonly string[] DiagnosableRoutes =
    [
        "/api/pages", "/api/spaces", "/api/tree", "/api/tags", "/api/space-trash", "/api/space-covers", "/api/attachments", "/api/objects",
        "/api/boards", "/api/projector", "/api/search", "/api/shared", "/api/notifications", "/api/me", "/api/analytics", "/api/ai",
        "/api/assist", "/api/assistant",
    ];

    public static async Task InvokeAsync(HttpContext context, Func<Task> next, ILogger logger)
    {
        try
        {
            await next();
        }
        catch (Exception e) when (context.RequestAborted.IsCancellationRequested && (e is OperationCanceledException or IOException))
        {
            // A browser cancelling omnisearch or a download is not a server failure.
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }
        }
        catch (BadHttpRequestException e) when (e.InnerException is JsonException json)
        {
            // A body that does not read as its request (an unescaped \ of a Windows path, a text for a number): name the field.
            var field = json.Path is { Length: > 2 } path && path.StartsWith("$.", StringComparison.Ordinal) ? path[2..] : null;
            var values = new Dictionary<string, object?> { ["field"] = field };
            await ProblemResponse.WriteAsync(context, StatusCodes.Status400BadRequest, field is null
                    ? "Tělo požadavku není platný JSON."
                    : "Pole {field} v těle požadavku není platný JSON nebo má jiný typ hodnoty (zpětné lomítko v cestě se píše jako \\\\).",
                "invalid_argument", values, values);
        }
        catch (BadHttpRequestException e)
        {
            await WriteAsync(context, Results.Problem(statusCode: e.StatusCode, title: Translate(context,
                e.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Tělo požadavku překračuje povolenou velikost." : "Požadavek není platný.")));
        }
        catch (ApiException e)
        {
            // An API client has no editor to keep its changes in: its conflict is worded for it.
            var message = context.Request.Path.StartsWithSegments("/api/v1") ? Rules.ForApiClient(e.Template) : e.Template;
            await ProblemResponse.WriteAsync(context, e.Status, message, e.Code, e.Extensions, e.Args);
        }
        catch (AiCallException)
        {
            // A model call failed mid-request (AI-25): the task is unavailable for this request, not a server error. The
            // exception carries only its code; the pipeline has already logged it.
            await ProblemResponse.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, AiGate.UnavailableTitle, AiGate.UnavailableCode);
        }
        catch (AntiforgeryValidationException)
        {
            await WriteAsync(context, Results.Problem(statusCode: 400, title: Translate(context, "Ověření požadavku vypršelo. Obnovte stránku.")));
        }
        catch (Exception e)
        {
            var path = context.Request.Path;
            // An allow-list (SEC-A-8): an ORM or driver message may quote the parameters, so a new route that carries passwords,
            // keys or tokens logs only the exception type until it is named here as a content route.
            var sensitive = path.StartsWithSegments("/signin-oidc") || path.StartsWithSegments("/mcp")
                || path.StartsWithSegments("/api") && !DiagnosableRoutes.Any(route => path.StartsWithSegments(route));
            if (sensitive)
            {
                logger.LogError("Request {TraceId} failed ({ErrorType}); the details stay out of the log on this route", context.TraceIdentifier, e.GetType().Name);
            }
            else
            {
                logger.LogError(e, "Request {TraceId} failed", context.TraceIdentifier);
            }
            await WriteAsync(context, path.StartsWithSegments("/signin-oidc")
                ? Results.Redirect("/?authError=external_login_failed")
                : Results.Problem(statusCode: 500, title: Translate(context, "Požadavek se nepodařilo dokončit."),
                    extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }));
        }
    }

    private static string Translate(HttpContext context, string czech) => ErrorTexts.Translate(RequestLanguage.Of(context), czech);

    // ProblemResponse writes the one shared problem+json shape. The branches below need shapes it
    // deliberately does not model: the traceId extension and the OIDC redirect, so they keep this writer.
    private static Task WriteAsync(HttpContext context, IResult result)
    {
        if (!context.Response.HasStarted)
        {
            return result.ExecuteAsync(context);
        }
        context.Abort();
        return Task.CompletedTask;
    }
}
