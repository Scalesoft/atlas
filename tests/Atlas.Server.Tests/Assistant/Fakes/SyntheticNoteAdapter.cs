using System.Collections.Concurrent;
using Atlas.Server.Features.Assistant.Sources;

namespace Atlas.Server.Tests.Assistant.Fakes;

/// <summary>A note of the synthetic second source type: readable by the listed users only, at its current revision.</summary>
public sealed record SyntheticNote(string Key, string Title, string Text, int Revision, IReadOnlySet<Guid> Readers, double Rank);

/// <summary>
/// The in-memory notes of <see cref="SyntheticNoteAdapter"/>, shared by every scope of a test host. A test adds, revises and
/// revokes notes while the model call is held by <c>FakeAiHold</c>.
/// </summary>
public sealed class SyntheticNotes
{
    private readonly ConcurrentDictionary<string, SyntheticNote> m_notes = new();

    public void Put(SyntheticNote note) => m_notes[note.Key] = note;

    public SyntheticNote? Get(string key) => m_notes.GetValueOrDefault(key);

    /// <summary>Revocation: the user no longer reads the note.</summary>
    public void Revoke(string key, Guid userId)
    {
        if (m_notes.TryGetValue(key, out var note))
        {
            m_notes[key] = note with { Readers = note.Readers.Where(id => id != userId).ToHashSet() };
        }
    }

    public IReadOnlyCollection<SyntheticNote> All => m_notes.Values.ToArray();

}

/// <summary>
/// AI-11 (spec RAG §1): the synthetic second source type, test-only and not a product feature. Type <c>note</c>, version 1; a
/// reference is the note key and its revision, without an anchor; its dependency columns are the external key and revision.
/// Registered only by the hosts of the extensibility tests, next to the page adapter; the common flow does not change.
/// </summary>
public sealed class SyntheticNoteAdapter(SyntheticNotes notes, ICurrentActor actor) : IKnowledgeSourceAdapter
{
    public const string SourceType = "note";
    public const int SourceVersion = 1;

    public string Type => SourceType;
    public int Version => SourceVersion;

    public Task<IReadOnlyList<AuthorizedSource>> RetrieveAsync(AnswerRetrievalRequest request, CancellationToken ct)
    {
        var words = request.Question.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => word.Length >= 3).ToArray();
        IReadOnlyList<AuthorizedSource> sources = notes.All
            .Where(note => note.Readers.Contains(request.Actor.Id) && words.Any(word => note.Text.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(note => note.Rank).ThenBy(note => note.Key, StringComparer.Ordinal)
            .Take(request.Take)
            .Select(note => new AuthorizedSource(Reference(note), note.Title, null, null, null, note.Text, note.Rank, null))
            .ToArray();
        return Task.FromResult(sources);
    }

    public Task<IReadOnlyDictionary<SourceReference, string?>> LoadTextAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
    {
        IReadOnlyDictionary<SourceReference, string?> result = refs.Distinct().ToDictionary(reference => reference,
            reference => Current(reference) is { } note ? note.Text : null);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> AuthorizeAsync(IReadOnlyList<SourceReference> refs,
        bool requireCurrentRevision, CancellationToken ct)
    {
        var user = actor.Claimed().Id;
        IReadOnlyDictionary<SourceReference, SourceStatus> result = refs.Distinct().ToDictionary(reference => reference, reference =>
            notes.Get(reference.Id) is not { } note || !note.Readers.Contains(user) ? SourceStatus.Unavailable
            : note.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) == reference.Revision || !requireCurrentRevision ? SourceStatus.Available
            : SourceStatus.Changed);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<SourceReference, SourceStatus>> StatusAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
    {
        var user = actor.Claimed().Id;
        IReadOnlyDictionary<SourceReference, SourceStatus> result = refs.Distinct().ToDictionary(reference => reference, reference =>
            notes.Get(reference.Id) is not { } note || !note.Readers.Contains(user) ? SourceStatus.Unavailable
            : note.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) == reference.Revision ? SourceStatus.Available
            : SourceStatus.Changed);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyDictionary<SourceReference, SourceDescription?>> DescribeAsync(IReadOnlyList<SourceReference> refs, CancellationToken ct)
    {
        IReadOnlyDictionary<SourceReference, SourceDescription?> result = refs.Distinct().ToDictionary(reference => reference,
            reference => notes.Get(reference.Id) is { } note ? new SourceDescription(note.Title, null, null, null, null) : null);
        return Task.FromResult(result);
    }

    public SourceColumns Columns(AuthorizedSource source)
        => new(SourceType, SourceVersion, null, null, source.Reference.Id, source.Reference.Revision, null, null, source.Reference.Anchor);

    public SourceReference? Reference(SourceColumns columns)
        => columns.SourceType == SourceType && columns.SourceVersion == SourceVersion && columns.ExternalKey is { } key
            ? new SourceReference(SourceType, SourceVersion, key, columns.ExternalRevision ?? "", columns.HeadingId)
            : null;

    public static SourceReference Reference(SyntheticNote note)
        => new(SourceType, SourceVersion, note.Key, note.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture), null);

    private SyntheticNote? Current(SourceReference reference)
        => notes.Get(reference.Id) is { } note && note.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture) == reference.Revision ? note : null;
}
