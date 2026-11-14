using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Atlas.Server;
using Atlas.Server.Features.ModuleSettings;
using Atlas.Server.Features.ModuleSettings.Contracts;
using Atlas.Server.Features.ModuleSettings.Domain;
using Xunit;

public class ModuleSettingsTests
{
    [Fact] public void Catalog_rejects_duplicate_module_ids()
    {
        var descriptor = new ModuleSettingsDescriptor("oidc", "OIDC", "Identity", "oidc", 10, true);
        Assert.Throws<InvalidOperationException>(() => new ModuleSettingsCatalog([descriptor, descriptor]));
    }
    [Fact] public void Catalog_orders_setup_steps_and_keeps_admin_only_modules_out_of_wizard()
    {
        var catalog = new ModuleSettingsCatalog([
            new("later", "Later", "", "later", 30, false), new("youtrack", "YouTrack", "", "youtrack", 20, true),
            new("oidc", "OIDC", "", "oidc", 10, true)]);
        Assert.Equal(["oidc", "youtrack"], catalog.Items.Where(x => x.ShowInSetup).Select(x => x.Id));
    }
    [Fact] public void Empty_secret_preserves_existing_secret_but_explicit_clear_removes_it()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
        var stored = protector.Protect("existing-secret");
        Assert.Equal(stored, OidcSecrets.Resolve(null, false, stored, protector));
        Assert.Equal(stored, OidcSecrets.Resolve("", false, stored, protector));
        Assert.Null(OidcSecrets.Resolve(null, true, stored, protector));
        Assert.Equal("replacement", protector.Unprotect(OidcSecrets.Resolve("replacement", false, stored, protector)!));
    }
    [Fact] public void Secret_replacement_and_clear_are_mutually_exclusive()
    {
        var protector = new EphemeralDataProtectionProvider().CreateProtector("test");
        Assert.Throws<ApiException>(() => OidcSecrets.Resolve("secret", true, null, protector));
    }
    [Fact] public void Provider_secret_is_not_in_diagnostic_string()
    {
        var request = new SaveOidcProvider("hub", "Hub", "https://hub.example", "client", "top-secret", false, ["openid"], false);
        Assert.DoesNotContain("top-secret", request.ToString());
    }
}
