namespace Atlas.Server.Features.Access.Contracts;

/// <summary>A locked row of who has access without a grant here ("Správci wiki", "Správci prostoru"); Count counts active people.</summary>
public sealed record AutomaticAccess(string Label, string Detail, string Level, int Count);
