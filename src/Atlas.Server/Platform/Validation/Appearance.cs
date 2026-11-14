namespace Atlas.Server.Platform.Validation;

/// <summary>User interface preference stored per account. Allowed values are validated by Rules.ValidateAppearance.
/// <paramref name="Motion"/> is "system" (follow the device) or "reduce" (always reduced motion); a client that
/// does not send it keeps the default. <paramref name="OpenLocked"/> opens pages with editing locked (Atlas 3.6). <paramref name="DragPages"/>
/// lets the page tree move pages by dragging („Přesouvat stránky tažením“, 3.6); off, they move by the menu and the keyboard only.
/// <paramref name="Language"/> is the language of the tool („Jazyk“, 4.1): "auto" (the browser decides), "cs" or "en".</summary>
public record Appearance(string Layout, string Accent, string Mode, string Motion = "system", bool OpenLocked = false, bool DragPages = true, string Language = "auto")
{
    public static Appearance Default => new("connected", "ocean", "system");
}
