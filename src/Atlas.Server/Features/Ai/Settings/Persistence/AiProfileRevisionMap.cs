using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using Atlas.Server.Features.Ai.Settings.Domain;

namespace Atlas.Server.Features.Ai.Settings.Persistence;

/// <summary>Insert-only: NHibernate never issues an UPDATE, and IntegrityRules refuses a delete.</summary>
public sealed class AiProfileRevisionMap : ClassMapping<AiProfileRevision>
{
    public AiProfileRevisionMap()
    {
        Table("`AiProfileRevision`");
        Mutable(false);
        Id(x => x.Id, m =>
        {
            m.Column("`Id`");
            m.Generator(Generators.Assigned);
        });
        Property(x => x.Kind, m => m.Length(16));
        Property(x => x.Enabled);
        Property(x => x.Provider, m => m.Length(32));
        Property(x => x.Location, m => m.Length(16));
        Property(x => x.BaseUrl, m => m.Length(2048));
        Property(x => x.Model, m => m.Length(200));
        Property(x => x.ModelVersion, m => m.Length(200));
        Property(x => x.ProtectedApiKey, m => m.Type(NHibernateUtil.StringClob));
        Property(x => x.TimeoutSeconds);
        Property(x => x.MaxConcurrency);
        Property(x => x.ReasoningEffort, m => m.Length(8));
        Property(x => x.Temperature);
        Property(x => x.NativeJsonSchema);
        Property(x => x.Dimensions);
        Property(x => x.QueryPrefix, m => m.Length(200));
        Property(x => x.DocumentPrefix, m => m.Length(200));
        Property(x => x.RequestDimensions);
        Property(x => x.UseChatProfile);
        Property(x => x.InputPricePerMillion, m => { m.Precision(12); m.Scale(4); });
        Property(x => x.OutputPricePerMillion, m => { m.Precision(12); m.Scale(4); });
        Property(x => x.Fingerprint, m => m.Length(64));
        Property(x => x.CreatedById);
        Property(x => x.CreatedAt, m => m.Type(NHibernateUtil.UtcDateTime));
    }
}
