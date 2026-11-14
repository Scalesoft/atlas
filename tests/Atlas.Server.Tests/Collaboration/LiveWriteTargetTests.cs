using Atlas.Server.Features.Collaboration.Domain;
using Xunit;

namespace Atlas.Server.Tests.Collaboration;

/// <summary>3.6-H: a live write is refused only when what it changes was changed since the agent read it, never for typing elsewhere.</summary>
public sealed class LiveWriteTargetTests
{
    private static string Doc(params string[] texts)
        => "{\"format\":\"atlas-rich\",\"version\":1,\"plugins\":[],\"document\":{\"type\":\"doc\",\"content\":["
           + string.Join(',', texts.Select(text => $"{{\"type\":\"paragraph\",\"content\":[{{\"type\":\"text\",\"text\":\"{text}\"}}]}}")) + "]}}";

    [Fact]
    public void Typing_elsewhere_keeps_the_target()
    {
        // Read: A B C. Since then a person changed A; the agent rewrites C (computed on the current document).
        Assert.True(LiveWriteTarget.Unchanged(Doc("A", "B", "C"), Doc("A typed", "B", "C"), Doc("A typed", "B", "C by AI"), wholeDocument: false));
        // A pure insertion touches nothing.
        Assert.True(LiveWriteTarget.Unchanged(Doc("A", "B"), Doc("A typed", "B"), Doc("A typed", "New", "B"), wholeDocument: false));
    }

    [Fact]
    public void A_target_changed_since_the_read_is_refused()
    {
        Assert.False(LiveWriteTarget.Unchanged(Doc("A", "B", "C"), Doc("A", "B", "C typed"), Doc("A", "B", "C by AI"), wholeDocument: false));
        // A block removed by the agent that a person added after the read.
        Assert.False(LiveWriteTarget.Unchanged(Doc("A", "B"), Doc("A", "Added", "B"), Doc("A", "B"), wholeDocument: false));
    }

    [Fact]
    public void A_whole_document_write_needs_the_whole_document_unchanged_and_key_order_is_no_change()
    {
        Assert.False(LiveWriteTarget.Unchanged(Doc("A", "B"), Doc("A typed", "B"), Doc("X"), wholeDocument: true));
        var reordered = Doc("A", "B").Replace("{\"type\":\"paragraph\",\"content\":", "{\"content\":").Replace("}]}", "}],\"type\":\"paragraph\"}");
        Assert.True(LiveWriteTarget.Unchanged(Doc("A", "B"), reordered, Doc("X"), wholeDocument: true));
    }

    [Fact]
    public void Default_attributes_the_editor_projects_are_no_change()
    {
        var projected = Doc("A", "B").Replace("{\"type\":\"paragraph\",", "{\"type\":\"paragraph\",\"attrs\":{\"textAlign\":null},");
        Assert.True(LiveWriteTarget.Unchanged(Doc("A", "B"), projected, Doc("A", "X"), wholeDocument: true));
        Assert.True(LiveWriteTarget.Unchanged(Doc("A", "B"), projected, projected.Replace("\"B\"", "\"X\""), wholeDocument: false));
    }

    [Fact]
    public void Undo_puts_back_only_what_the_revision_changed()
    {
        // The revision rewrote B; since then a person changed A and added D.
        Assert.Equal(Doc("A typed", "B", "C", "D"), LiveWriteTarget.Undo(Doc("A typed", "B by AI", "C", "D"), Doc("A", "B by AI", "C"), Doc("A", "B", "C")));
        // A revision that only removed B: it goes back after A.
        Assert.Equal(Doc("A typed", "B", "C"), LiveWriteTarget.Undo(Doc("A typed", "C"), Doc("A typed", "C"), Doc("A typed", "B", "C")));
        // What it changed was changed again since: nothing to undo safely.
        Assert.Null(LiveWriteTarget.Undo(Doc("A", "B edited", "C"), Doc("A", "B by AI", "C"), Doc("A", "B", "C")));
    }
}
