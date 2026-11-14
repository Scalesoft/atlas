using Atlas.Server.Features.ContentImport.Contracts;

namespace Atlas.Server.Features.ContentImport.Domain;

public static class ImportReportBuilder
{
    private const string Preserved = "Původní soubor zůstává v ZIP; není automaticky převeden ani zveřejněn.";

    public static WikiImportReport Build(Guid id, string token, DateTime expires, Guid? space, string name, WikiSourceArchive archive, List<ImportPlan> plans)
    {
        var used = plans.SelectMany(p => p.Assets.Values).Select(a => a.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A file that became a page is reported as that page, never a second time as a preserved original.
        var consumed = plans.Select(p => p.Source.SourceFilePath).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var renamedFrom = archive.Renamed.Where(x => archive.Files.ContainsKey(x.Value)).ToDictionary(x => x.Value, x => x.Key, StringComparer.OrdinalIgnoreCase);
        var pages = plans.Select(p => p.Assets.Values.Where(a => SensitiveFiles.IsSensitive(a.Path)).Select(a => a.Path).Distinct().Order(StringComparer.Ordinal).ToArray() is { Length: > 0 } sensitive
            ? p.Item with { Warnings = [.. p.Item.Warnings, .. sensitive.Select(x => "Stránka připojuje soubor „" + x + "“, který vypadá jako certifikát nebo privátní klíč.")] }
            : p.Item);
        var files = archive.Files.Values.Where(f => !consumed.Contains(f.Path)).Select(f =>
        {
            var copied = used.Contains(f.Path);
            var warnings = new List<string>();
            if (renamedFrom.TryGetValue(f.Path, out var original))
            {
                warnings.Add("Cesta „" + original + "“ se od jiné položky ZIP liší jen velikostí písmen; soubor je veden jako „" + f.Path + "“.");
            }
            if (SensitiveFiles.IsSensitive(f.Path))
            {
                warnings.Add(copied ? "Soubor vypadá jako certifikát nebo privátní klíč a stane se přílohou stránky; ověřte, že jej smí vidět její čtenáři."
                    : "Soubor vypadá jako certifikát nebo privátní klíč; zvažte, zda má zůstat i v původním ZIP.");
            }
            if (!copied)
            {
                if (archive.Skipped.TryGetValue(f.Path, out var reason))
                {
                    warnings.Add(reason);
                }
                warnings.Add(Preserved);
            }
            return new WikiImportItem(f.Path, copied ? "asset" : "unsupported", copied ? "copy" : "preserve", null, null, null, f.Sha256, warnings, [], []);
        });
        var excluded = archive.Excluded.Select(x => new WikiImportItem(x.Key, "unsupported", "preserve", null, null, null, "", [x.Value, Preserved], [], []));
        var items = pages.Concat(files).Concat(excluded).ToArray();
        var counts = new WikiImportCounts(items.Count(i => i.Action == "create"), items.Count(i => i.Action == "update"), items.Count(i => i.Action == "skip"), items.Count(i => i.Action == "conflict"), items.Count(i => i.Kind == "unsupported"), used.Count);
        List<string> warnings =
        [
            "Git/storage export nemusí obsahovat všechny stránky, přílohy, historii ani pravidla oprávnění Wiki.js. Ověřte jeho úplnost proti zdroji.",
            "Všechny stránky vzniknou jako neveřejné koncepty bez přenesení ACL. Publikaci a přístupy správce nastaví až po kontrole.",
            "U každé stránky je uvedeno, co se nepřeneslo beze změny (typ, počet, první řádek). Odkazy na nadpisy míří na odpovídající nadpis cílové stránky; nenalezené kotvy a nepřiřazené wiki odkazy jsou v reportu. Původní ZIP je zachován.",
        ];
        if (archive.StrippedPrefix is { } prefix)
        {
            warnings.Add("Úvodní složka ZIP „" + prefix + "“ není součástí cest stránek; odkazy na původní wiki se párují bez ní.");
        }
        if (archive.Files.Keys.Count(SensitiveFiles.IsSensitive) is var sensitiveCount and > 0)
        {
            warnings.Add("Souborů, které vypadají jako certifikát nebo privátní klíč: " + sensitiveCount + ". Jsou označené v seznamu; přílohou se stanou jen ty, na které odkazuje stránka.");
        }
        if (archive.Renamed.Count > 0)
        {
            warnings.Add("Některé cesty se liší jen velikostí písmen; druhá z nich dostala číslovanou cestu a je označená v seznamu.");
        }
        return new WikiImportReport(id, token, expires, space, name, counts, items, warnings, plans.Count > 0 && counts.Conflict == 0);
    }
}
