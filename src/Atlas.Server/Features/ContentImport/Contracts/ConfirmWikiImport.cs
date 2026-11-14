namespace Atlas.Server.Features.ContentImport.Contracts;

public sealed record ConfirmWikiImport(string ReviewToken, bool AcknowledgeQuarantine, bool AcknowledgeWarnings)
{
    /// <summary>ReviewToken is a capability secret; the compiler-generated record ToString() would otherwise print it into logs.</summary>
    public override string ToString()
    {
        return $"ConfirmWikiImport {{ ReviewToken = ***, AcknowledgeQuarantine = {AcknowledgeQuarantine}, AcknowledgeWarnings = {AcknowledgeWarnings} }}";
    }
}
