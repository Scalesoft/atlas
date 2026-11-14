namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>Insert-only fan-out of a settings revision. The composite key is the natural one; NHibernate needs value equality for it.</summary>
public class OidcProviderSettings
{
    public virtual Guid SettingsId { get; set; }
    public virtual string ProviderId { get; set; } = "";
    public virtual string DisplayName { get; set; } = "";
    public virtual string Issuer { get; set; } = "";
    public virtual string ClientId { get; set; } = "";
    public virtual string? ProtectedSecret { get; set; }
    public virtual bool AllowSignup { get; set; }

    public override bool Equals(object? other)
    {
        return other is OidcProviderSettings settings
            && settings.SettingsId == SettingsId
            && string.Equals(settings.ProviderId, ProviderId, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(SettingsId, ProviderId);
    }
}
