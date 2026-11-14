using Atlas.Server.Features.ModuleSettings.Contracts;

namespace Atlas.Server.Features.ModuleSettings;

public static class ModuleSettingsModule
{
    public static IServiceCollection AddModuleSettings(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(new ModuleSettingsDescriptor("oidc", "Firemní přihlášení", "Připojte JetBrains Hub nebo jiného poskytovatele OpenID Connect.", "oidc", 10, true));
        if (config.GetValue("YouTrack:Enabled", true))
        {
            services.AddSingleton(new ModuleSettingsDescriptor("youtrack", "YouTrack", "Propojte články s úkoly a povolte projekty pro jednotlivé prostory.", "youtrack", 20, true));
        }
        services.AddSingleton(new ModuleSettingsDescriptor("editor-assist", "Návrhy při psaní", "Pravidlové nápovědy v editoru, například podobné stránky a kontrola odkazů. Nevolají žádný model.", "editor-assist", 25, false));
        services.AddSingleton(new ModuleSettingsDescriptor("wiki-js-import", "Import Wiki.js", "Zkontrolujte a bezpečně přeneste existující obsah ze ZIP exportu.", "wiki-js-import", 30, true));
        services.AddSingleton<ModuleSettingsCatalog>();
        services.AddScoped<ModuleSettingsAccess>();
        services.AddHttpClient("oidc-settings-discovery", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
