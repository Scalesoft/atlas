using Atlas.Server.Features.Projector.Contracts;

namespace Atlas.Server.Features.Projector.Domain;

/// <summary>
/// Which catalog definitions an insertion adds (spec §1.4, §4): the wanted keys and what they require, depth first so that every
/// dependency comes before what needs it and a shared one comes once. Keys already on the page are skipped with their dependencies
/// (the page has dealt with them; a wanted one is reported), and one marked not_applicable there is reported to what needs it; a key the catalog does not know
/// is a warning; a cycle refuses the whole insertion with its path. A key the catalog has twice takes its first definition in
/// catalog order (the page marked as a catalog first, then document order) with a warning. The walk keeps its own stack, bounded
/// in depth and in what one insertion adds, so a long chain on a big catalog page is a 400, never a stack overflow.
/// </summary>
public static class RequirementResolver
{
    public const int MaxDepth = 64;
    public const int MaxItems = 1000;
    public const string InCatalog = "v katalogu";
    public const string OnSourcePage = "na zdrojové stránce";

    /// <summary>The catalog without repeated keys, and a warning for each repeated one.</summary>
    public static (IReadOnlyList<CatalogDefinition> Unique, IReadOnlyList<string> Warnings) Distinct(IReadOnlyList<CatalogDefinition> catalog)
    {
        var groups = catalog.GroupBy(x => x.Key, StringComparer.Ordinal).ToArray();
        return (groups.Select(group => group.First()).ToArray(), groups.Where(group => group.Count() > 1).Select(group => Repeated(group.First())).ToArray());
    }

    /// <param name="source">Where the definitions come from, as a warning about a missing one says it: the catalog, or the page inserted from.</param>
    public static RequirementResolution Resolve(IReadOnlyList<CatalogDefinition> catalog, IReadOnlyList<string> wanted, IReadOnlyCollection<string> present,
        IReadOnlyCollection<string>? notApplicable = null, string source = InCatalog)
    {
        var byKey = catalog.GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var dismissed = new HashSet<string>(notApplicable ?? [], StringComparer.Ordinal);
        var done = new HashSet<string>(present, StringComparer.Ordinal);
        // The definitions being visited, outermost first, with the index of the next dependency each still has to visit.
        var stack = new List<(CatalogDefinition Definition, CatalogDefinition? Dependent, int Next)>();
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<ResolvedRequirement>();
        var warnings = new List<string>();

        void Enter(string key, CatalogDefinition? dependent)
        {
            // The path first: a key on it is already marked done.
            if (onPath.Contains(key))
            {
                var cycle = stack.Select(x => x.Definition.Key).SkipWhile(x => x != key).Append(key).ToArray();
                throw new ApiException(400, $"Závislosti požadavků tvoří kruh: {string.Join(" → ", cycle)}.", "cyclic_dependency",
                    new Dictionary<string, object?> { ["path"] = cycle });
            }
            if (dismissed.Remove(key))
            {
                warnings.Add($"Požadavek „{key}“ už na stránce je, ale je označený jako nerelevantní"
                    + (dependent is null ? "." : $"; „{dependent.Title}“ na něm závisí."));
            }
            else if (dependent is null && present.Contains(key))
            {
                // Asked for by name, so the user learns why nothing came of it; a dependency already there is no news.
                warnings.Add($"Požadavek „{key}“ už na stránce je; znovu se nevloží.");
            }
            // A block of another page (key@pageId) is no definition to insert: the copy keeps it as a dependency on that page.
            if (!done.Add(key) || RequirementAttributes.CrossPage(key) is not null)
            {
                return;
            }
            if (!byKey.TryGetValue(key, out var definitions))
            {
                warnings.Add($"Požadavek „{key}“ {source} není.");
                return;
            }
            if (definitions.Length > 1)
            {
                warnings.Add(Repeated(definitions[0]));
            }
            if (stack.Count == MaxDepth || items.Count + stack.Count == MaxItems)
            {
                throw new ApiException(400, $"Vložení by přidalo víc než {MaxItems} požadavků nebo řetěz závislostí delší než {MaxDepth}; vložte menší část katalogu.",
                    "dependency_too_deep");
            }
            stack.Add((definitions[0], dependent, 0));
            onPath.Add(key);
        }

        foreach (var key in wanted)
        {
            Enter(key.Trim(), null);
            while (stack.Count > 0)
            {
                var (definition, dependent, next) = stack[^1];
                if (next < definition.Requires.Count)
                {
                    stack[^1] = (definition, dependent, next + 1);
                    Enter(definition.Requires[next], definition);
                    continue;
                }
                stack.RemoveAt(stack.Count - 1);
                onPath.Remove(definition.Key);
                items.Add(new ResolvedRequirement(definition, dependent?.Key, dependent is null ? "Vybráno" : $"Potřebuje ho: {dependent.Title}"));
            }
        }
        return new RequirementResolution(items, warnings);
    }

    private static string Repeated(CatalogDefinition first) => $"Klíč „{first.Key}“ je v katalogu víckrát; platí první výskyt („{first.Title}“).";
}
