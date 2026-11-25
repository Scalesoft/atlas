namespace Atlas.Server.Features.PersonalLibrary;

public sealed record GetActivity(DateOnly From, DateOnly To, string Scope);
public sealed record GetActivityChanges(DateOnly Day, string Scope, int Offset = 0);
public sealed record GetPageReadership(Guid PageId, DateOnly From, DateOnly To, int Offset = 0);
public sealed record GetPopularPages(DateOnly From, DateOnly To);
public sealed record ActivityDay(string Day, long Count);
public sealed record ActivityAuthor(Guid UserId, string UserName, long Revisions, long Pages);
public sealed record ActivitySummary(long Revisions, long Pages, IReadOnlyList<ActivityDay> Days, IReadOnlyList<ActivityAuthor> Authors);
public sealed record ActivityChange(Guid PageId, string Title, Guid AuthorId, string AuthorName, DateTime At, int Version);
public sealed record ActivityChanges(IReadOnlyList<ActivityChange> Items, bool HasMore);
/// <summary>One day of Čtenost stránky: views and, for managers of the vault, secret releases on the page.</summary>
public sealed record ReadershipDay(string Day, long Views, long Secrets);
/// <summary>A view (Kind view) or one release of secret fields on the page (Kind secret: SecretTitle, Fields in registry order,
/// Operation reveal or copy). Secret entries are listed only for the managers of their vault.</summary>
public sealed record ReadershipItem(string Kind, Guid UserId, string UserName, DateTime At, string? SecretTitle = null,
    IReadOnlyList<string>? Fields = null, string? Operation = null);
/// <summary>Secrets: the released secret entries of the period the caller may see (see ReadershipItem).</summary>
public sealed record PageReadership(long Views, long Readers, long Secrets, IReadOnlyList<ReadershipDay> Days, IReadOnlyList<ReadershipItem> Items,
    bool HasMore);
public sealed record PopularPage(Guid PageId, string Title, long Views, long Readers);
public sealed record PopularPages(IReadOnlyList<PopularPage> Items);
