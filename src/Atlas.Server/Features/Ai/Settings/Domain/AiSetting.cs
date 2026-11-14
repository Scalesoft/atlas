namespace Atlas.Server.Features.Ai.Settings.Domain;

/// <summary>The master switch of AI for the whole wiki. One current row; no row means Vypnuto (AI-24).</summary>
public class AiSetting
{
    public virtual Guid Id { get; set; }
    public virtual bool Enabled { get; set; }
    public virtual Guid ChangedById { get; set; }
    public virtual DateTime ActiveFrom { get; set; }
    public virtual DateTime? ActiveTo { get; set; }
}
