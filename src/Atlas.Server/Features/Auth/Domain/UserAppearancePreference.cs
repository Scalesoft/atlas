namespace Atlas.Server.Features.Auth.Domain;

public class UserAppearancePreference
{
    public virtual Guid Id { get; set; } = Guid.NewGuid();

    public virtual DateTime ActiveFrom { get; set; } = DateTime.UtcNow;

    public virtual DateTime? ActiveTo { get; set; }

    public virtual Guid UserId { get; set; }

    public virtual string Layout { get; set; } = "connected";

    public virtual string Accent { get; set; } = "ocean";

    public virtual string Mode { get; set; } = "system";

    /// <summary>"system" follows the device's reduced-motion setting; "reduce" always reduces motion.</summary>
    public virtual string Motion { get; set; } = "system";

    public virtual bool OpenLocked { get; set; }

    public virtual bool DragPages { get; set; } = true;

    /// <summary>"auto" leaves the language of the tool to the browser; "cs" or "en" fixes it (4.1).</summary>
    public virtual string Language { get; set; } = "auto";

    public virtual Appearance Appearance => new(Layout, Accent, Mode, Motion, OpenLocked, DragPages, Language);
}
