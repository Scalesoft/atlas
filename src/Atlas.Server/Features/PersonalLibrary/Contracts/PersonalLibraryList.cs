namespace Atlas.Server.Features.PersonalLibrary.Contracts;

public sealed record PersonalLibraryList(IReadOnlyList<PersonalLibraryItem> Items, bool HasMore, int? NextOffset);
