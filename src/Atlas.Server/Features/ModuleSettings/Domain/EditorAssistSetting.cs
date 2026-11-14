namespace Atlas.Server.Features.ModuleSettings.Domain;

/// <summary>The wiki-level switch of the module "Návrhy při psaní". One current row; no row means the default, on.</summary>
public class EditorAssistSetting
{
    public virtual Guid Id { get; set; }
    public virtual bool Enabled { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
