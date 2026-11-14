using OpenIddict.Abstractions;
using OpenIddict.Client;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Platform.Jobs;

namespace Atlas.Server.Features.Auth.OpenIdConnect;

public static class OidcModule
{
    public static IServiceCollection AddOpenIdConnect(this IServiceCollection services, IConfiguration config, IHostEnvironment environment, AtlasOidcOptions? runtimeOptions = null)
    {
        var settings = runtimeOptions ?? AtlasOidcOptions.Read(config);
        settings.Validate();
        services.AddSingleton(settings);
        services.AddLogging(logging => logging.AddFilter("OpenIddict", LogLevel.Warning));
        if (settings.Providers.Length == 0)
        {
            return services;
        }
        services.AddOpenIddict().AddCore(core => core.SetDefaultTokenEntity<OidcStateToken>()
                .ReplaceTokenStore<OidcStateToken, NHibernateOidcTokenStore>().DisableEntityCaching())
            .AddClient(client =>
            {
                client.AllowAuthorizationCodeFlow();
                client.DisableWebServicesFederationClaimMapping();
                client.AddEventHandler<OpenIddictClientEvents.ProcessChallengeContext>(handler => handler.SetOrder(OpenIddictClientHandlers.AttachCodeChallengeParameters.Descriptor.Order - 500).UseInlineHandler(context =>
                {
                    context.CodeChallengeMethod = OpenIddictConstants.CodeChallengeMethods.Sha256;
                    return default;
                }));
                client.SetStateTokenLifetime(TimeSpan.FromMinutes(10));
                client.UseDataProtection();
                if (environment.IsDevelopment() && string.IsNullOrEmpty(settings.SigningCertificatePath) && string.IsNullOrEmpty(settings.EncryptionCertificatePath))
                {
                    client.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
                }
                else
                {
                    var certificates = OidcCertificates.Load(config, environment, settings);
                    foreach (var certificate in certificates.Signing)
                    {
                        client.AddSigningCertificate(certificate);
                    }
                    foreach (var certificate in certificates.Encryption)
                    {
                        client.AddEncryptionCertificate(certificate);
                    }
                }
                client.UseAspNetCore().EnableRedirectionEndpointPassthrough().EnableErrorPassthrough();
                client.UseSystemNetHttp();
                foreach (var provider in settings.Providers)
                {
                    var registration = new OpenIddictClientRegistration
                    {
                        RegistrationId = provider.Id,
                        ProviderName = provider.Id,
                        Issuer = new Uri(provider.Issuer),
                        ClientId = provider.ClientId,
                        ClientSecret = provider.ClientSecret,
                        RedirectUri = new Uri(new Uri(settings.PublicOrigin), "/signin-oidc/" + provider.Id)
                    };
                    registration.Scopes.UnionWith(provider.Scopes);
                    registration.Scopes.Add(OpenIddictConstants.Scopes.OpenId);
                    client.AddRegistration(registration);
                }
            });
        services.AddJob<OidcTokenCleanup>();
        return services;
    }
}
