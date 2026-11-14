namespace Atlas.Server.Features.Ai.Settings.Contracts;

public sealed record DiscoverAiModelsRequest(string Provider, string Location, string? BaseUrl, string? ApiKey, bool ClearApiKey)
{
    public override string ToString() => "DiscoverAiModelsRequest { ApiKey = [redacted] }";
}
