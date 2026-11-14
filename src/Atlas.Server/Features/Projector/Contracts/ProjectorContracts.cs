using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Atlas.Server.Features.ApiActivity;

namespace Atlas.Server.Features.Projector.Contracts;

/// <summary>A requirement block as the index and the queries read it (spec §2.1); <see cref="Requires"/> parsed from the comma list.
/// <see cref="WaitingOn"/>: the keys it requires whose blocks on the same page are not done yet (only for a block not done itself).</summary>
public sealed record Requirement(string BlockId, string Key, string Title, string Direction, string State, string Date, string Party, string Where,
    IReadOnlyList<string> Requires, string? ParentBlockId, int Position)
{
    public IReadOnlyList<string> WaitingOn { get; init; } = [];

    /// <summary>The contact the party is (plan 3.1 §2.3 point 1), its id as stored; "" for a party written as text only.</summary>
    public string PartyContactId { get; init; } = "";

    /// <summary>The block's own word for its state (plan 3.4 §4), "" for the state's own; <see cref="State"/> still counts. Read from the
    /// page's content in a page's read only (the index keeps none), left out elsewhere.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StateLabel { get; init; }

    /// <summary>The external service an input comes through (ÚZIS, NIA…, plan 3.4 §5), as <see cref="StateLabel"/> only in a page's read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

    /// <summary>The day it was checked off (yyyy-MM-dd, the facts line's „hotovo“, 3.5's completion date), read from the page's content only; left out without one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Completed { get; init; }

    /// <summary>Who changed the block last and when: the newest revision up to the one shown (working for its editor, published for anyone
    /// else) whose change summary touched it, or the newest revision saved without a summary when that is newer; left out when neither is.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RequirementChangeInfo? Changed { get; init; }

    /// <summary>The block's section on its page, the heading texts above it outermost first (RequirementText.Sectioned, the one rule of
    /// sections); in a page's read and the overview's cells, left out elsewhere.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Section { get; init; }
}

/// <summary>Who changed a block last: <see cref="Kind"/> save (read in the working revision) or publish (in the publication); who made
/// that version of the block and when, with the block's title and state as shown.</summary>
public sealed record RequirementChangeInfo(string Kind, Guid ChangedById, string ChangedBy, DateTime ChangedAt, string Title, string State);

/// <summary>One record of a block's history. <see cref="Kind"/> <c>note</c>: a dated line of its notes as written (<see cref="Date"/>,
/// <see cref="Time"/> when written, Europe/Prague, and <see cref="Text"/>). <c>state</c>: a version of the page changed its state
/// <see cref="From"/> → <see cref="To"/>, saved by <see cref="ChangedBy"/> at <see cref="ChangedAt"/> (UTC; Date and Time in Prague).</summary>
public sealed record RequirementHistoryEntry(string Kind, string Date, string? Time)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Text { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? From { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? To { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Guid? ChangedById { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ChangedBy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTime? ChangedAt { get; init; }
}

/// <summary>A block's history, newest first, read from the revision the reader is shown (the working one for an editor of the page, the
/// publication for anyone else): the dated lines of its notes and the state changes of the page's versions up to that revision.</summary>
public sealed record RequirementHistory(Guid PageId, string BlockId, IReadOnlyList<RequirementHistoryEntry> Entries);

/// <summary>One prerequisite of another page (<c>key@pageId</c>) as the reader sees it: <see cref="Readable"/> false for a page they may
/// not read, a missing or trashed one, or a key the page does not hold; then nothing else of it is told and it counts as unknown.</summary>
public sealed record RequirementPrerequisite(string Reference, Guid PageId, string Key, bool Readable)
{
    public string? PageTitle { get; init; }
    public string? BlockId { get; init; }
    public string? Title { get; init; }
    public string? State { get; init; }
    public string? Direction { get; init; }
}

/// <summary>„Co to blokuje“: the blocks of other pages the reader may read that require this one (<c>key@pageId</c>).</summary>
public sealed record RequirementDependents(Guid PageId, string Key, IReadOnlyList<RequirementRow> Rows);

/// <summary>The cycle a new dependency would close, as references (<c>key</c> on the page, <c>key@pageId</c> elsewhere), or none.</summary>
public sealed record RequirementCycle(IReadOnlyList<string>? Path);

/// <summary>The summary block's numbers (spec §1.2): <see cref="Total"/> counts every block, <see cref="Applicable"/> those that are
/// not not_applicable, the "z N" the web and the projection show; <see cref="Percent"/> is <see cref="Done"/> out of
/// <see cref="Applicable"/>, rounded down (0 when none applies); <see cref="Waiting"/> counts waiting_for_input;
/// <see cref="InboundMissing"/> are the inbound blocks neither done nor not_applicable.</summary>
public sealed record RequirementSummaryDto(int Total, int Done, int Percent, IReadOnlyDictionary<string, int> States, int Waiting,
    IReadOnlyList<Requirement> InboundMissing)
{
    public int Applicable { get; init; }
}

/// <param name="Working">Whether the blocks come from the working revision (an editor) or the publication (anyone else).</param>
/// <param name="Version">The page version a block command expects.</param>
public sealed record PageRequirements(Guid PageId, int Version, bool Working, IReadOnlyList<Requirement> Blocks, RequirementSummaryDto Summary);

/// <summary>One indexed block with the page it is on, as the space-wide queries read it; <see cref="RevisionId"/> is the revision the
/// reader was given (for the opening the read records); <see cref="PageDone"/> and <see cref="PageApplicable"/> count the page's
/// blocks, whatever the filter keeps.</summary>
public sealed record RequirementRow(Guid PageId, string PageTitle, Requirement Requirement)
{
    public Guid RevisionId { get; init; }
    public int PageDone { get; init; }
    public int PageApplicable { get; init; }
}

/// <summary>The matrix pages × keys: <see cref="Keys"/> ordered, each page's cells (its blocks that pass the filter) in key order.</summary>
public sealed record RequirementOverview(IReadOnlyList<string> Keys, IReadOnlyList<OverviewPage> Pages);

/// <summary><see cref="Percent"/> and <see cref="Applicable"/> are the page's summary block's (every block of the page, whatever the
/// filter keeps in <see cref="Cells"/>; review C2-13).</summary>
public sealed record OverviewPage(Guid PageId, string Title, IReadOnlyList<Requirement> Cells)
{
    public int Percent { get; init; }
    public int Applicable { get; init; }
}

/// <summary>"Na co čekáme": waiting_for_input blocks grouped by party, the longest waiting (oldest date, undated last) first.</summary>
public sealed record WaitingList(IReadOnlyList<WaitingParty> Parties);

/// <param name="ContactId">The contact the party is, when its blocks name one (they are grouped by it, whatever text they show).</param>
public sealed record WaitingParty(string Party, IReadOnlyList<WaitingItem> Items, Guid? ContactId = null);

public sealed record WaitingItem(Guid PageId, string PageTitle, string BlockId, string Key, string Title, string Direction, string State, string Date);

/// <summary>A predefined requirement of a catalog page (spec §1.4): a top-level block with a key. <see cref="ContentJson"/> is the
/// block node to insert (null in a catalog read without bodies); <see cref="Bundle"/> the text of the heading above it ("" before the
/// first heading); <see cref="ModuleKey"/> the stable key of that heading, the product or module (plan 3.1 §2.3, P1): its headingId,
/// else the slug of its text ("" before the first heading); <see cref="Category"/> the heading one level above the module's
/// (## Autentizace › ### NIA, plan 3.4 §5), <see cref="Domain.RequirementTree.Core"/> when there is none; <see cref="Product"/> the heading
/// one level above the category's (# Portál pacienta › ## Autentizace › ### NIA, plan 3.5 C), "" when there is none; <see cref="Summary"/> the
/// plain text of the paragraphs directly under the module's heading before its first requirement block (what the module is), joined by a
/// space, "" when there are none.</summary>
public sealed record CatalogDefinition(string Key, string Title, string TitleMarkdown, string Direction, IReadOnlyList<string> Requires,
    JsonObject? ContentJson, Guid PageId, string Bundle, string ModuleKey, string Category = Domain.RequirementTree.Core, string Product = "",
    string Summary = "");

/// <summary>The catalog of a space; <see cref="Warnings"/> name the keys defined more than once (the first definition counts).</summary>
public sealed record ProjectorCatalog(IReadOnlyList<CatalogDefinition> Definitions, IReadOnlyList<string> Warnings);

/// <param name="RequiredBy">The key that pulled it in as a dependency; null for a requested key.</param>
/// <param name="Reason">Why it is inserted, for the user ("Vybráno"; "Potřebuje ho: Objednávky" for a prerequisite of Objednávky).</param>
public sealed record ResolvedRequirement(CatalogDefinition Definition, string? RequiredBy, string Reason);

/// <summary>What to insert, dependencies first, and the keys the catalog does not know.</summary>
public sealed record RequirementResolution(IReadOnlyList<ResolvedRequirement> Items, IReadOnlyList<string> Warnings);

public sealed record CatalogPageState(Guid PageId, bool IsCatalog);

/// <summary>A block command's answer: the page version the next command expects. <see cref="Warnings"/> say what a change did to
/// other blocks (a renamed key moved their requires).</summary>
public sealed record RequirementWriteResult(Guid PageId, int Version) : IPageVersionResult
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <param name="Inserted">The inserted blocks in document order with their new ids; a key the page had was given a suffix.</param>
public sealed record RequirementInsertResult(Guid PageId, int Version, IReadOnlyList<InsertedRequirement> Inserted, IReadOnlyList<string> Warnings)
    : IPageVersionResult;

public sealed record InsertedRequirement(string Key, string BlockId, string? RequiredBy, string Reason);

/// <summary>A space page template that holds requirement blocks (plan 3.1 §2.3): a deployment preset; <see cref="Requirements"/> counts
/// its blocks in the revision the actor reads. For the settings page "Sady a katalog": <see cref="Summary"/> the template's first
/// non-empty paragraph, <see cref="Products"/> the products of its definitions (the category when a definition has no product but a
/// real category), <see cref="Modules"/> the number of its modules, <see cref="Differs"/> the titles of its blocks whose text differs
/// from the space catalog's definition of the same key.</summary>
public sealed record ProjectorPreset(Guid PageId, string Title, int Requirements, string Summary = "", IReadOnlyList<string>? Products = null,
    int Modules = 0, IReadOnlyList<string>? Differs = null);

/// <summary>One page of a space-wide requirement query: <see cref="Total"/> rows match, <see cref="NextOffset"/> is null on the last page.</summary>
public sealed record RequirementQueryPage(IReadOnlyList<RequirementRow> Rows, int Total, int? NextOffset);

/// <summary>Where a block goes: after a block (its sibling), as the last child of a block, at the end of a heading's section, or at
/// the end of the page when none is given. At most one may be given.</summary>
public sealed record RequirementPosition(string? AfterBlockId = null, string? IntoBlockId = null, string? HeadingId = null);

/// <summary>One block's change of <c>UpdateRequirements</c> (a contract, not a command: the command coverage test lists commands only).</summary>
public sealed record RequirementBlockChange(string BlockId, RequirementChange Change);

/// <summary>The attributes to change; null leaves one as it is. <see cref="Title"/> replaces the title paragraph with plain text;
/// <see cref="NotesMarkdown"/> everything under it, merged as page Markdown is (<c>RequirementText.ReplaceNotes</c>).
/// <see cref="PartyContactId"/> links the party to a directory contact ("" unlinks it), <see cref="PartyContact"/> to the contact an
/// identity names (as atlas_upsert_contact: found by the contacts' identity, else created); the party then reads the contact's name
/// unless <see cref="Party"/> says otherwise. A <see cref="Party"/> alone is text: it unlinks the contact.
/// <see cref="StateLabel"/> is the block's own word for its state ("" for the state's own; a new state without one clears it),
/// <see cref="System"/> the external service of an input.</summary>
public sealed record RequirementChange(string? Key = null, string? Title = null, string? Direction = null, string? State = null, string? Date = null,
    string? Party = null, string? Where = null, IReadOnlyList<string>? Requires = null, string? NotesMarkdown = null, string? PartyContactId = null,
    JsonObject? PartyContact = null, string? StateLabel = null, string? System = null);
