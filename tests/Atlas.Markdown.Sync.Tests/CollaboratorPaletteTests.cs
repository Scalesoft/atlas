namespace Atlas.Markdown.Sync.Tests;

/// <summary>The one palette of collaborator colours and its assignment per live document or board (Atlas 5.2).</summary>
public sealed class CollaboratorPaletteTests
{
    private static string Color(int slot) => CollaboratorPalette.Entries[slot].Light;

    [Fact]
    public void The_palette_has_the_twelve_appearance_hues_with_literal_colours_in_both_variants()
    {
        Assert.Equal(12, CollaboratorPalette.Count);
        Assert.Equal(12, CollaboratorPalette.Entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(CollaboratorPalette.Entries, entry => Assert.Matches("^#[0-9a-f]{6}$", entry.Light));
        Assert.All(CollaboratorPalette.Entries, entry => Assert.Matches("^#[0-9a-f]{6}$", entry.Dark));
    }

    [Fact]
    public void The_automatic_slot_is_the_editors_hash_of_the_user_id()
    {
        // The same arithmetic as markdownUserColorSlot (src/Atlas.Markdown/src/core/userColor.test.ts pins these values).
        Assert.Equal(4, CollaboratorPalette.Auto("verified-id"));
        Assert.Equal(1, CollaboratorPalette.Auto("00000000-0000-0000-0000-000000000001"));
        Assert.Equal(8, CollaboratorPalette.Auto("ffffffff-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void A_free_preferred_colour_is_kept_and_a_taken_one_moves_to_the_next_free_slot_in_palette_order()
    {
        Assert.Equal(3, CollaboratorPalette.Assign(3, new HashSet<int>()));
        Assert.Equal(4, CollaboratorPalette.Assign(3, new HashSet<int> { 3 }));
        Assert.Equal(6, CollaboratorPalette.Assign(3, new HashSet<int> { 3, 4, 5 }));
        Assert.Equal(0, CollaboratorPalette.Assign(11, new HashSet<int> { 11 }));
        Assert.Equal(7, CollaboratorPalette.Assign(7, Enumerable.Range(0, 12).ToHashSet()));
    }

    [Fact]
    public void Earlier_arrivals_keep_their_colour_and_a_leaver_frees_it()
    {
        var present = new List<(string UserId, string Color)>();
        string Join(string user, string? preferred)
        {
            var color = CollaboratorPalette.Effective(user, preferred, present);
            present.Add((user, color));
            return color;
        }
        var first = Join("a", Color(2));
        var second = Join("b", Color(2));
        Assert.Equal(Color(2), first);
        Assert.Equal(Color(3), second);
        Assert.Equal(first, Join("a", Color(2)));                          // another tab of the same person looks the same
        present.RemoveAll(person => person.UserId == "a");
        Assert.Equal(Color(3), present.Single().Color);                    // the one who stayed keeps theirs
        Assert.Equal(Color(2), Join("c", Color(2)));                       // and the freed colour can be had again
    }

    [Fact]
    public void Without_a_choice_the_automatic_colour_is_the_start_and_a_colour_outside_the_palette_stays()
    {
        Assert.Equal(Color(CollaboratorPalette.Auto("verified-id")), CollaboratorPalette.Effective("verified-id", null, []));
        Assert.Equal("#123456", CollaboratorPalette.Effective("host", "#123456", [("other", "#123456")]));
    }
}
