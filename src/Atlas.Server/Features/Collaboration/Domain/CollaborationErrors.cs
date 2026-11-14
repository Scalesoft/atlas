namespace Atlas.Server.Features.Collaboration.Domain;

public static class CollaborationErrors
{
    /// <summary>The code of <see cref="Stale"/>: the page moved to another generation, a writer of the old one rejoins (F6).</summary>
    public const string SupersededCode = "collaboration_superseded";

    public static ApiException Stale()
    {
        return new ApiException(409, "Společná editace má novější stav. Obnovte připojení k dokumentu.", SupersededCode);
    }
}
