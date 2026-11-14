namespace Atlas.Server.Features.Access.Contracts;

/// <summary>A person or group on a resource. Detail: the username (people) or the member count as text (groups).</summary>
public sealed record AccessSubject(string Kind, Guid Id, string Name, string? Detail, int? MemberCount = null, bool? IsActive = null);
