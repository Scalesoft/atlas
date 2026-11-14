namespace Atlas.Server.Features.PersonalLibrary.Queries;

/// <param name="Filter">all, ai (changes with AI only) or mine (pages the caller owns, has written in or keeps among favourites).</param>
/// <param name="Cursor">Next of the previous page, or null for the first.</param>
public sealed record ListRecentChanges(string Filter, string? Cursor, int Limit);
