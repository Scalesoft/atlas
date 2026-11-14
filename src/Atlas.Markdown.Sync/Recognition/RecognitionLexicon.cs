namespace Atlas.Markdown.Sync.Recognition;

/// <summary>A person of a place's lexicon (server <c>LexiconPerson</c>, editor <c>MarkdownLexiconPerson</c>).</summary>
public sealed record RecognitionPerson(string Key, Guid? ContactId, Guid? UserId, string Name, IReadOnlyList<string> Aliases);

/// <summary>A page of a place's lexicon.</summary>
public sealed record RecognitionPage(Guid Id, string Title);

/// <summary>
/// What a place knows by name, the shape of <c>GET /api/spaces/{id}/lexicon</c>; <paramref name="Truncated"/>: entries were left out,
/// so nothing becomes a chip by itself.
/// </summary>
public sealed record RecognitionLexicon(bool Truncated, IReadOnlyList<RecognitionPerson> People, IReadOnlyList<RecognitionPage> Pages, IReadOnlyList<string> YouTrackProjects)
{
    /// <summary>An alias shorter than this (folded, without spaces) is not looked for: „MM“ is two people's initials and a common pair.</summary>
    private const int MinAlias = 3;

    private static readonly IReadOnlySet<string> UnresolvedKinds = new HashSet<string>(StringComparer.Ordinal) { "person" };

    /// <summary>
    /// The entries of the editor's person and page recognizers (<c>personEntries</c>, the corpus's <c>pageEntries</c>): everyone by full
    /// name (a chip when the only one and the lexicon is whole) and by alias and initials (only offered, D4); a page by its title (only
    /// offered).
    /// </summary>
    public IEnumerable<RecognitionEntry> Entries()
    {
        foreach (var person in People)
        {
            yield return new RecognitionEntry("person", person.Key, person.Name, "full", !Truncated, Inflects: true);
            foreach (var alias in person.Aliases.Concat(Initials(person.Name)))
            {
                var key = RecognitionText.NameKey(alias);
                if (key.Replace(" ", "").Length < MinAlias)
                {
                    continue;
                }
                yield return new RecognitionEntry("person", person.Key, alias.Trim(), "alias", false, Cased: !key.Contains(' ') && key.Length <= MinAlias);
            }
        }
        foreach (var page in Pages)
        {
            yield return new RecognitionEntry("page", page.Id.ToString(), page.Title, "full", false);
        }
    }

    /// <summary>The index of <see cref="Entries"/>, people unresolved when several share a name.</summary>
    public RecognitionIndex Index() => new(Entries(), UnresolvedKinds);

    /// <summary>A name's initials as written in capitals („Jan Pavel Novák“ → „JPN“), from three words up; never stored.</summary>
    private static IEnumerable<string> Initials(string name)
    {
        var words = name.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= MinAlias ? [string.Concat(words.Select(word => char.ToUpperInvariant(word[0])))] : [];
    }
}
