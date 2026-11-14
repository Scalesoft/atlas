namespace Atlas.Server.Features.Auth.Queries;

/// <summary>Whether the single Workspace row exists. Never null, so InvokeAsync&lt;T&gt; always has a response.</summary>
public sealed record WorkspaceState(bool Configured, string? Name);
