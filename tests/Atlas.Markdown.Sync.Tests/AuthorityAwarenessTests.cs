using System.Text;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

public sealed class AuthorityAwarenessTests
{
    private static readonly CollabUser User = new("verified-id", "Žluťoučký", "#123456");
    internal static byte[] Frame(string state, long client = 42, long clock = 1, int count = 1)
    {
        using var output = new MemoryStream();
        Write(output, count); Write(output, client); Write(output, clock);
        var bytes = Encoding.UTF8.GetBytes(state); Write(output, bytes.Length); output.Write(bytes);
        return output.ToArray();
    }
    private static void Write(Stream output, long value)
    { while (value > 127) { output.WriteByte((byte)((value & 127) | 128)); value >>= 7; } output.WriteByte((byte)value); }
    [Fact]
    public void Replaces_claimed_identity_with_authenticated_identity_and_preserves_cursor()
    {
        var input = Frame("{\"user\":{\"id\":\"forged\",\"name\":\"Forged\"},\"cursor\":{\"anchor\":null}}");
        var result = AuthorityAwareness.Validate(input, User, 16384);
        Assert.Equal(42, result.ClientId);
        var json = Encoding.UTF8.GetString(result.Payload);
        Assert.DoesNotContain("Forged", json); Assert.Contains("verified-id", json); Assert.Contains("cursor", json);
        Assert.NotEqual(input, result.Payload);
    }
    [Fact]
    public void Keeps_only_the_identity_of_a_read_only_participant()
    {
        var input = Frame("{\"user\":{\"id\":\"forged\"},\"cursor\":{\"anchor\":1},\"selection\":{}}");
        var json = Encoding.UTF8.GetString(AuthorityAwareness.Validate(input, User, 16384, presenceOnly: true).Payload);
        Assert.Contains("verified-id", json); Assert.DoesNotContain("cursor", json); Assert.DoesNotContain("selection", json);
        Assert.Equal(42, AuthorityAwareness.Validate(Frame("null"), User, 16384, presenceOnly: true).ClientId);
    }
    [Theory]
    [InlineData("null")][InlineData("{}")][InlineData("{\"cursor\":null}")]
    public void Accepts_owned_removal_and_bounded_state(string json)
        => Assert.Equal(42, AuthorityAwareness.Validate(Frame(json), User, 16384).ClientId);
    [Theory]
    [InlineData("[]")][InlineData("5")][InlineData("\"text\"")][InlineData("{")][InlineData("{\"a\":1,\"a\":2}")]
    public void Rejects_invalid_root_or_duplicate_fields(string json)
        => Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate(Frame(json), User, 16384));
    [Theory]
    [InlineData(0)][InlineData(2)][InlineData(128)]
    public void Rejects_any_count_other_than_one(int count)
        => Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate(Frame("null", count: count), User, 16384));
    [Fact]
    public void Rejects_trailing_bytes_truncation_and_unsafe_varints()
    {
        var valid = Frame("null");
        Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate([.. valid, 0], User, 16384));
        Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate(valid[..^1], User, 16384));
        Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate([1, 255, 255, 255, 255, 255, 255, 255, 127], User, 16384));
    }
    [Fact]
    public void Rejects_invalid_UTF8_and_both_input_and_rewritten_output_over_budget()
    {
        Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate([1, 1, 1, 1, 255], User, 16384));
        Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate(Frame("null"), User, 1));
        Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate(Frame("{}"), User, 10));
    }
    [Fact]
    public void Rejects_excessive_JSON_depth()
    {
        var json = new string('[', 15) + "null" + new string(']', 15);
        Assert.Throws<InvalidDataException>(() => AuthorityAwareness.Validate(Frame("{\"cursor\":" + json + "}"), User, 16384));
    }
    [Fact]
    public void Stamps_the_colour_the_document_assigned_never_the_one_the_client_claims()
    {
        string Written(CollabUser user, string? assigned)
            => Encoding.UTF8.GetString(AuthorityAwareness.Validate(Frame("{\"user\":{\"color\":\"#2a9d8f\"}}"), user, 16384, color: assigned).Payload);
        Assert.Contains("\"color\":\"#1863b6\"", Written(User, "#1863b6"));
        Assert.Contains("\"color\":\"#123456\"", Written(User, null));
        Assert.Contains("\"color\":\"#888888\"", Written(new CollabUser("verified-id", "Name"), null));
    }
}
