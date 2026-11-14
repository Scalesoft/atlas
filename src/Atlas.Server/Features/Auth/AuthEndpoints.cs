using Microsoft.AspNetCore.Authentication;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Commands;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Developer;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.Auth.Recovery;
using Atlas.Server.Features.Security.Commands;
using Atlas.Server.Features.Setup.Commands;
using Atlas.Server.Features.Setup.Contracts;
using Wolverine;

namespace Atlas.Server.Features.Auth;

public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/setup/status", async (IMessageBus bus, ServerAccessTokens tokens, BuiltinPasswordPolicy passwordPolicy, CancellationToken ct) =>
        {
            try
            {
                var state = await bus.InvokeAsync<WorkspaceState>(new GetWorkspaceState(), ct);
                if (!state.Configured)
                {
                    await tokens.EnsureBootstrapAsync(ct);
                }
                return Results.Ok(new SetupStatusDto(true, !state.Configured, true, passwordPolicy.MinimumLength, state.Name, state.Configured ? null : tokens.SetupSource, AtlasVersion.Current));
            }
            catch (ApiException e) when (e.Status == 503)
            {
                return Results.Ok(new SetupUnavailableDto(false, true, true, e.Message, AtlasVersion.Current));
            }
        });
        app.MapPost("/api/setup/complete", async (CompleteSetup command, IMessageBus bus, ISignInService signIn, BuiltinAuthenticationOptions builtinOptions, HttpContext context, CancellationToken ct) =>
        {
            if (!builtinOptions.Enabled)
            {
                throw new ApiException(409, "Pro úvodní nastavení nejprve zapněte builtin přihlášení, poté propojte účet správce s firemním poskytovatelem.");
            }
            var user = await bus.InvokeAsync<UserAccount>(command, ct);
            await signIn.SignInAsync(context, user);
            return Results.Ok(await bus.InvokeAsync<UserDto>(new GetCurrentUser(), ct));
        }).RequireRateLimiting("authentication").Produces<UserDto>();
        app.MapDeveloperSignIn();

        app.MapPost("/api/auth/logout", async (HttpContext context, IMessageBus bus, CancellationToken ct) =>
        {
            // The session's re-authentication window ends with it, on the server.
            await bus.InvokeAsync(new EndReauthWindow(), ct);
            await context.SignOutAsync();
            return Results.NoContent();
        });

        app.MapGet("/api/auth/recovery/status", async (ServerAccessTokens tokens, CancellationToken ct)
                => Results.Ok(await tokens.GetRecoveryStatusAsync(ct)))
            .RequireRateLimiting("authentication")
            .Produces<RecoveryStatus>();
        app.MapPost("/api/auth/recovery", async (RecoverAdministrator request, ServerAccessTokens tokens, ISignInService signIn, IMessageBus bus, HttpContext context, CancellationToken ct) =>
            {
                var user = await tokens.RecoverAsync(request, ct);
                await signIn.SignInAsync(context, user, "recovery");
                return Results.Ok(await bus.InvokeAsync<UserDto>(new GetCurrentUser(), ct));
            })
            .RequireRateLimiting("activation")
            .Produces<UserDto>();

        // RequireAuthorization is the sanctioned deviation (spec §2): the cookie handler answers 401
        // exactly as CurrentUser.GetAsync did, only earlier.
        var profile = app.MapGroup("").RequireAuthorization();
        profile.MapGet("/api/auth/me", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<UserDto>(new GetCurrentUser(), ct)))
            .Produces<UserDto>();
        profile.MapPost("/api/profile/sessions/end-others", async (IMessageBus bus, ISignInService signIn, HttpContext context, CancellationToken ct) =>
        {
            await signIn.RefreshSignInAsync(context, await bus.InvokeAsync<UserAccount>(new EndOtherSessions(), ct));
            return Results.NoContent();
        });
        profile.MapGet("/api/profile/appearance", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok((await bus.InvokeAsync<UserDto>(new GetCurrentUser(), ct)).Appearance))
            .Produces<Appearance>();
        profile.MapPut("/api/profile/appearance", async (Appearance appearance, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<Appearance>(new UpdateAppearance(appearance), ct)))
            .Produces<Appearance>();
        profile.MapPut("/api/profile/release-notes", async (ReleaseNotesSeenRequest request, IMessageBus bus, CancellationToken ct) =>
        {
            await bus.InvokeAsync(new MarkReleaseNotesSeen(request.Version), ct);
            return Results.NoContent();
        });
        profile.MapGet("/api/me/ai-preferences", async (IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AiPreferencesView>(new GetAiPreferences(), ct)))
            .Produces<AiPreferencesView>();
        profile.MapPut("/api/me/ai-preferences", async (SaveAiPreferencesRequest request, IMessageBus bus, CancellationToken ct)
                => Results.Ok(await bus.InvokeAsync<AiPreferencesView>(new SaveAiPreferences(request), ct)))
            .Produces<AiPreferencesView>();
    }
}
