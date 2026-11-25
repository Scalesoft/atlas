namespace Atlas.Server.Features.PersonalLibrary.Queries;

/// <param name="Filter">all, ai (changes with AI only) or mine (pages the caller owns, has written in or keeps among favourites).</param>
/// <param name="Cursor">Next of the previous page, or null for the first.</param>
/// <param name="Author">Only the changes of this account (a person's activity: the account is the contact's directoryId).</param>
/// <param name="Space">Only the pages of this space.</param>
public sealed record ListRecentChanges(string Filter, string? Cursor, int Limit, Guid? Author = null, Guid? Space = null);

/// <summary>A person's year by day and space (their profile's calendar and spaces), of what the caller may see; the caller's own without
/// <paramref name="Author"/>.</summary>
public sealed record GetPersonActivity(Guid? Author);
