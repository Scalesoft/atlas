namespace Atlas.Server.Features.Collaboration.Contracts;

/// <summary>Envelope: a handler returning a bare bool would look like a cascading message to Wolverine.</summary>
public sealed record ExternalChange(bool Changed);
