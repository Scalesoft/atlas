namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>Insert-only scope of one provider in one revision. The whole triple is the key, so equality spans all three.</summary>
public class OidcProviderScope
{
    public virtual Guid SettingsId { get; set; }
    public virtual string ProviderId { get; set; } = "";
    public virtual string Scope { get; set; } = "";

    public override bool Equals(object? other)
    {
        return other is OidcProviderScope scope
            && scope.SettingsId == SettingsId
            && string.Equals(scope.ProviderId, ProviderId, StringComparison.Ordinal)
            && string.Equals(scope.Scope, Scope, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(SettingsId, ProviderId, Scope);
    }
}
