using Microsoft.AspNetCore.Identity;
using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Recovery;

namespace Atlas.Server.Features.Auth;

public static class AuthModule
{
    /// <summary>Authentication services without any HTTP scheme, so test hosts can use them too.</summary>
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.Configure<PasswordHasherOptions>(options => options.IterationCount = 210_000);
        services.AddScoped<ISignInService, CookieSignInService>();
        services.AddScoped<CredentialAudit>();
        services.AddSingleton(provider => new BuiltinAuthenticationOptions(provider.GetRequiredService<IConfiguration>().GetValue("Authentication:Builtin:Enabled", true)));
        services.AddSingleton(provider => new BuiltinPasswordPolicy(
            provider.GetRequiredService<IHostEnvironment>().IsDevelopment(),
            provider.GetRequiredService<IConfiguration>().GetValue("Authentication:Builtin:AllowDevelopmentPasswords", false)));
        services.AddSingleton<BuiltinLoginThrottle>();
        services.AddSingleton(provider => new RecoveryOptions { Username = provider.GetRequiredService<IConfiguration>()["Recovery:Username"] });
        services.AddSingleton<ServerTokenFiles>();
        services.AddSingleton<ServerAccessTokens>();
        return services;
    }

    /// <summary>The retry loop that publishes server capabilities. Tests leave it out so token rotation and logging stay deterministic.</summary>
    public static IServiceCollection AddAuthBackgroundServices(this IServiceCollection services)
    {
        services.AddHostedService<ServerTokenInitializer>();
        return services;
    }
}
