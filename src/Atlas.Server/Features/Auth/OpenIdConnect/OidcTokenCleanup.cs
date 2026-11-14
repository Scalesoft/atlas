using OpenIddict.Abstractions;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Auth.OpenIdConnect;

/// <summary>Hourly prune of state tokens that are neither valid nor unexpired any more.</summary>
internal sealed class OidcTokenCleanup : IJobDefinition
{
    public string Id => "oidc.cleanup";

    public string Title => "Úklid přihlašovacího stavu";

    public string Description => "Smaže stavové tokeny přihlášení přes OpenID Connect, které jsou starší než den a jsou už použité nebo vypršely.";

    public JobSchedule? Schedule => JobSchedule.Every(TimeSpan.FromHours(1));

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        try
        {
            await context.Services.GetRequiredService<IOpenIddictTokenStore<OidcStateToken>>().PruneAsync(DateTimeOffset.UtcNow.AddDays(-1), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only, as before: the exception of a sign-in store may quote a state.
            throw new JobFailedException($"OIDC state cleanup failed ({ex.GetType().Name})", retry: true);
        }
    }
}
