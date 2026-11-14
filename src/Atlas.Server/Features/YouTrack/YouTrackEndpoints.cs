using Atlas.Server.Features.YouTrack.Commands;
using Atlas.Server.Features.YouTrack.Contracts;
using Atlas.Server.Features.YouTrack.Queries;
using Atlas.Server.Platform.Hosting;
using Atlas.Server.Platform.Localization;
using Atlas.Server.Platform.Validation;
using Atlas.YouTrack;
using Wolverine;

namespace Atlas.Server.Features.YouTrack;

public static class YouTrackEndpoints
{
    public static IEndpointRouteBuilder MapYouTrack(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup("/api/admin/integrations/youtrack").RequireAuthorization();
        admin.MapGet("", async (IMessageBus bus, CancellationToken ct) =>
        {
            return Results.Ok(await bus.InvokeAsync<YouTrackSettingsDto>(new GetYouTrackSettings(), ct));
        });
        admin.MapPut("", async (SaveYouTrackSettingsRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            return Results.Ok(await bus.InvokeAsync<YouTrackSettingsDto>(new SaveYouTrackSettings(request), ct));
        });
        admin.MapPost("/test", async (YouTrackReadRunner runner, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var result = await runner.TestConnectionAsync(ct);
                return Results.Ok(new { ok = true, checkedAt = DateTime.UtcNow, message = ErrorTexts.Translate(RequestLanguage.Of(http), "Spojení funguje. Vyberte projekty povolené pro jednotlivé prostory."), projects = result.Projects });
            }
            catch (YouTrackUnavailableException)
            {
                throw Unavailable();
            }
        }).RequireRateLimiting("authentication");
        var reader = endpoints.MapGroup("/api/integrations/youtrack").RequireAuthorization();
        reader.MapGet("/search", (Guid spaceId, string q, YouTrackReadRunner runner, CancellationToken ct) => SearchAsync(runner, spaceId, q, ct));
        reader.MapGet("", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<YouTrackReaderSetup>(new ListYouTrackSpaces(), ct)));
        // The bare list of spaces, as the editor and the omnisearch read it until they take the address from the route above.
        reader.MapGet("/spaces", async (IMessageBus bus, CancellationToken ct) =>
            Results.Ok((await bus.InvokeAsync<YouTrackReaderSetup>(new ListYouTrackSpaces(), ct)).SpaceIds));
        reader.MapGet("/resolve", (Guid spaceId, string reference, YouTrackReadRunner runner, CancellationToken ct) => ResolveAsync(runner, spaceId, reference, ct));
        reader.MapPost("/resolve", (Guid spaceId, YouTrackResolveRequest request, YouTrackReadRunner runner, CancellationToken ct) => ResolveManyAsync(runner, spaceId, request, ct));
        return endpoints;
    }

    /// <summary>The editor's YouTrack search for a space; also the board-bound variant's (BoardEndpoints), with the board's space.</summary>
    public static async Task<IResult> SearchAsync(YouTrackReadRunner runner, Guid spaceId, string query, CancellationToken ct)
    {
        try
        {
            return Results.Ok((await runner.SearchAsync(spaceId, query, ct)).Items);
        }
        catch (YouTrackUnavailableException)
        {
            throw Unavailable();
        }
        catch (ArgumentException)
        {
            throw InvalidReference();
        }
    }

    /// <summary>The editor's issue resolution for a space; also the board-bound variant's (BoardEndpoints).</summary>
    public static async Task<IResult> ResolveAsync(YouTrackReadRunner runner, Guid spaceId, string reference, CancellationToken ct)
    {
        try
        {
            var result = await runner.ResolveAsync(spaceId, reference, ct);
            return result.Issue is null
                ? throw new ApiException(404, "Úkol nebyl nalezen v povolených projektech.")
                : Results.Ok(result.Issue);
        }
        catch (YouTrackUnavailableException)
        {
            throw Unavailable();
        }
        catch (ArgumentException)
        {
            throw InvalidReference();
        }
    }

    /// <summary>Up to 100 references in one answer, in their order: the issue, or null for one that is not in the allowed projects; also
    /// the board-bound variant's (BoardEndpoints).</summary>
    public static async Task<IResult> ResolveManyAsync(YouTrackReadRunner runner, Guid spaceId, YouTrackResolveRequest request, CancellationToken ct)
    {
        if (request.References is not { Length: > 0 and <= 100 } references || references.Any(reference => reference is null))
        {
            throw new ApiException(400, "Najednou lze ověřit 1 až 100 úkolů.");
        }
        try
        {
            return Results.Ok(await runner.ResolveManyAsync(spaceId, references, ct));
        }
        catch (YouTrackUnavailableException)
        {
            throw Unavailable();
        }
        catch (ArgumentException)
        {
            throw InvalidReference();
        }
    }

    private static ApiException Unavailable()
        => new(503, "YouTrack není dostupný. Zkontrolujte uložené spojení, oprávnění servisního účtu a povolené projekty, nebo zkuste požadavek později.");

    private static ApiException InvalidReference()
        => new(400, "Použijte výraz do 256 znaků, ID úkolu nebo odkaz z nakonfigurovaného YouTrack.");
}
