namespace Atlas.Server.Platform.Content;

/// <summary>
/// A node the Markdown cannot carry, written as <c>⟦c:<see cref="Index"/>⟧</c>: inline inside a text block, or alone on its
/// line for a block. <see cref="Label"/> is readable and never carries what the reader may not see: a password reference is
/// only "Odkaz na heslo", an attachment names its file without the URL.
/// </summary>
public sealed record ComponentSlot(int Index, string Type, string Label, bool Inline)
{
    /// <summary>
    /// The page's own attachments the component embeds (an image, a file card, or such nodes inside a table or a callout), in
    /// document order, each with its occurrence among the embeds of the same attachment on the page: what the embed tools address.
    /// Only ids of canonical attachment URLs; never a URL.
    /// </summary>
    public IReadOnlyList<ComponentEmbed> Embeds { get; init; } = [];

    /// <summary>
    /// The component as a reader reads it, for a block Markdown cannot edit but a model must read (a Projector requirement with its notes
    /// and nested blocks, the deployment summary); null for any other. Read-only: an edit goes through the component's own tools.
    /// </summary>
    public string? Text { get; init; }

    public bool Equals(ComponentSlot? other)
    {
        return other is not null && Index == other.Index && Type == other.Type && Label == other.Label && Inline == other.Inline
            && Embeds.SequenceEqual(other.Embeds) && Text == other.Text;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Index, Type, Label, Inline, Embeds.Count);
    }
}

/// <summary>One embed of a <see cref="ComponentSlot"/>: the attachment, how it is shown and its occurrence on the page (from 1).</summary>
public sealed record ComponentEmbed(Guid AttachmentId, EmbedKind Kind, int Occurrence);
