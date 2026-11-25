namespace Atlas.Server.Features.Administration.Contracts;

/// <param name="ContactId">The directory contact the account is (a profile's „Vytvořit účet“): linked in the same transaction, or nothing is made.</param>
public sealed record CreateBuiltinUser(string Username, string DisplayName, bool IsWikiAdmin = false, Guid? ContactId = null);
