using NHibernate.Mapping.ByCode;

namespace Atlas.Server.Platform.Persistence;

/// <summary>Builds the model mapper from every ClassMapping&lt;T&gt; in this assembly. Property columns are quoted so the schema keeps its PascalCase names.</summary>
public static class AtlasMappings
{
    public static ModelMapper Create()
    {
        var mapper = new ModelMapper();
        mapper.BeforeMapProperty += (_, property, mapping) => mapping.Column("`" + property.LocalMember.Name + "`");
        mapper.AddMappings(typeof(AtlasMappings).Assembly.GetExportedTypes());
        return mapper;
    }
}
