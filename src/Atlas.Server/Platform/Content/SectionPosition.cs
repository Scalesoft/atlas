namespace Atlas.Server.Platform.Content;

/// <summary>
/// Where <see cref="RichMarkdownMerge.InsertSection"/> puts a new section: at the start or the end of the page, or right
/// after the whole section (subsections included) of a given heading.
/// </summary>
public enum SectionPosition
{
    Start,
    End,
    AfterSection,
}
