namespace Atlas.Server.Features.PersonalApi.Contracts;

public sealed record CreatedApiToken(ApiTokenDto Token, string Secret)
{
    public override string ToString() => $"CreatedApiToken {{ Id = {Token.Id}, secret redacted }}";
}
