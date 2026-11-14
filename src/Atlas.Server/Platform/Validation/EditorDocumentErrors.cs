using Atlas.Markdown.Sync;

namespace Atlas.Server.Platform.Validation;

/// <summary>
/// Atlas's wording of the editor package's verdict. The editor owns which documents are valid (Atlas.Markdown.Sync's
/// <see cref="MarkdownDocumentValidator"/>, the manifest of the pinned editor release, so an editor bump brings its rules);
/// Atlas keeps its API contract: 400 <c>invalid_document</c>, a Czech text naming the JSON path and the path as a problem
/// extension. The package's English message follows in parentheses, since it names what was expected (known types, the
/// content expression, the allowed values) and a client needs that to repair the document.
/// </summary>
public static class EditorDocumentErrors
{
    /// <summary>The Czech reason of a violation (after its path), e.g. for the content validation report.</summary>
    public static string Reason(MarkdownDocumentViolation violation) => $"{violation.Code switch
    {
        MarkdownDocumentViolationCodes.InvalidJson => $"není platný JSON nebo je zanořen hlouběji než {MarkdownDocumentValidator.MaxDepth} úrovní uzlů",
        MarkdownDocumentViolationCodes.InvalidEnvelope => "neodpovídá obálce dokumentu {format: \"atlas-rich\", version: 1, plugins, document}",
        MarkdownDocumentViolationCodes.InvalidPlugins => "není platný požadavek na plugin {id, version} nebo je plugin uveden vícekrát",
        MarkdownDocumentViolationCodes.UnknownPlugin => "uvádí plugin, který editor Atlasu nezná",
        MarkdownDocumentViolationCodes.UnsupportedPluginVersion => "uvádí verzi pluginu, kterou editor nepřevede",
        MarkdownDocumentViolationCodes.MissingPlugin => "patří pluginu, který dokument neuvádí v poli plugins",
        MarkdownDocumentViolationCodes.InvalidNode => "musí být objekt uzlu {type, attrs, content, marks} s názvem typu",
        MarkdownDocumentViolationCodes.UnknownNode => "není typ uzlu editoru",
        MarkdownDocumentViolationCodes.UnknownMark => "není značka editoru",
        MarkdownDocumentViolationCodes.UnknownField => "není podporované pole",
        MarkdownDocumentViolationCodes.DuplicateField => "je uvedeno vícekrát",
        MarkdownDocumentViolationCodes.UnknownAttribute => "není atribut tohoto uzlu nebo značky",
        MarkdownDocumentViolationCodes.InvalidAttribute => "má neplatnou hodnotu",
        MarkdownDocumentViolationCodes.MissingAttribute => "je povinný",
        MarkdownDocumentViolationCodes.InvalidText => "musí být neprázdný text",
        MarkdownDocumentViolationCodes.InvalidContent => "má obsah, který typ uzlu nepovoluje",
        MarkdownDocumentViolationCodes.InvalidMarks => "musí být pole značek",
        MarkdownDocumentViolationCodes.MarkNotAllowed => "nese značku, kterou nadřazený uzel ve svém obsahu nepovoluje",
        MarkdownDocumentViolationCodes.MarkConflict => "nese značku vícekrát nebo se značkou, se kterou ji nelze kombinovat",
        MarkdownDocumentViolationCodes.TooDeep => $"je zanořen hlouběji než {MarkdownDocumentValidator.MaxDepth} úrovní",
        MarkdownDocumentViolationCodes.TableTooLarge => "překračuje velikost tabulky (nejvýše 1000 buněk včetně sloučených, nejvýše 1000 řádků či sloupců)",
        MarkdownDocumentViolationCodes.InvalidTable => "má neplatnou geometrii tabulky: buňky se překrývají, chybí nebo jejich šířky sloupců nesouhlasí",
        _ => "neodpovídá schématu editoru",
    }} ({violation.Message})";

    /// <summary>The 400 an API client gets: the path, the reason and what to do, plus the path as a problem extension.</summary>
    public static ApiException Error(MarkdownDocumentViolation violation) => new(400,
        $"Obsah editoru neodpovídá schématu editoru Atlasu: {violation.Path} {Reason(violation)}. Upravte dokument tak, aby obsahoval jen uzly, "
        + "značky a atributy, které editor zná (GET /api/v1/pages/{id} vrací platný tvar).", "invalid_document",
        new Dictionary<string, object?> { ["path"] = violation.Path });
}
