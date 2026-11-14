using System.Buffers.Binary;
using System.Text;
using Atlas.Server.Features.Vaults;
using Atlas.Server.Features.Vaults.Kdbx;
using Xunit;
using static Atlas.Server.Tests.Vaults.KdbxTestFiles;
using static Atlas.Server.Tests.Vaults.KdbxTestSealer;

namespace Atlas.Server.Tests.Vaults;

/// <summary>
/// Hostile but correctly authenticated payloads: what a person who knows the master password (or controls a
/// KeePass client) could place behind the HMACs. The parser must stay bounded and fail as KdbxException.
/// </summary>
public sealed class KdbxPayloadTests
{
    private const string Plain = "aeskdf-aes-none.kdbx";
    private const string Compressed = "aeskdf-chacha20-gzip.kdbx";

    [Theory]
    [InlineData(Plain)]
    [InlineData(Compressed)]
    public void Resealed_unchanged_payload_reads_like_the_original(string name)
    {
        var file = Read(name);
        var resealed = Reseal(file, Password(name), OpenPayload(file, Password(name)));
        using var database = Reader().Read(resealed, Password(name), false, CancellationToken.None);
        var expected = Fixture(name).GetProperty("entries")[0];
        Assert.Equal(expected.GetProperty("password").GetString(), Assert.Single(database.Entries).Password.Reveal());
    }

    [Fact]
    public void Document_type_definitions_and_entity_expansion_are_refused()
    {
        var bomb = "<!DOCTYPE KeePassFile [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\"><!ENTITY c \"&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;\">]>";
        Assert.Equal(KdbxFailure.Corrupt, FailsWith(Plain, xml => InsertAfterDeclaration(xml, bomb).Replace("<Generator>", "<Generator>&c;")).Failure);
        var external = "<!DOCTYPE KeePassFile [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]>";
        Assert.Equal(KdbxFailure.Corrupt, FailsWith(Plain, xml => InsertAfterDeclaration(xml, external).Replace("<Generator>", "<Generator>&x;")).Failure);
    }

    [Fact]
    public void Deep_nesting_in_a_skipped_subtree_is_rejected()
    {
        var deep = string.Concat(Enumerable.Repeat("<X>", 5000)) + string.Concat(Enumerable.Repeat("</X>", 5000));
        var failure = FailsWith(Compressed, xml => xml.Replace("</Meta>", deep + "</Meta>"));
        Assert.Equal(KdbxFailure.LimitExceeded, failure.Failure);
        Assert.Contains(nameof(VaultOptions.MaxGroupDepth), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_attribute_or_text_node_is_rejected_with_bounded_allocation()
    {
        var attribute = new string('A', 8 * 1024 * 1024);
        foreach (var mutation in new Func<string, string>[]
        {
            xml => xml.Replace("<Meta>", $"<Meta x=\"{attribute}\">"),
            xml => xml.Replace("</Meta>", $"<Huge>{attribute}</Huge></Meta>"),
        })
        {
            var file = Mutate(Compressed, mutation);
            var reader = Reader();
            var before = GC.GetAllocatedBytesForCurrentThread();
            var error = Record.Exception(() => reader.Read(file, Password(Compressed), false, CancellationToken.None));
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(KdbxFailure.LimitExceeded, Assert.IsType<KdbxException>(error).Failure);
            Assert.Contains(nameof(VaultOptions.MaxXmlFieldBytes), error.Message, StringComparison.Ordinal);
            // The node guard stops XmlReader at about 6 MiB of raw markup, well before 8 MiB.
            Assert.True(allocated < 64L * 1024 * 1024, $"Allocated {allocated} bytes.");
        }
    }

    [Fact]
    public void Protected_value_above_the_field_limit_is_rejected()
    {
        var value = Convert.ToBase64String(new byte[2 * 1024 * 1024]);
        var failure = FailsWith(Plain, xml => ReplaceFirstProtected(xml, value));
        Assert.Equal(KdbxFailure.LimitExceeded, failure.Failure);
    }

    [Fact]
    public void Entry_count_above_the_default_limit_is_rejected()
    {
        var entries = new StringBuilder();
        for (var i = 0; i < 50_001; i++)
        {
            entries.Append("<Entry><UUID>").Append(Convert.ToBase64String(Guid.NewGuid().ToByteArray())).Append("</UUID></Entry>");
        }
        var failure = FailsWith(Compressed, xml =>
        {
            // Directly after the root group's UUID.
            var at = xml.IndexOf("</UUID>", xml.IndexOf("<Root>", StringComparison.Ordinal), StringComparison.Ordinal) + "</UUID>".Length;
            return xml[..at] + entries + xml[at..];
        });
        Assert.Equal(KdbxFailure.LimitExceeded, failure.Failure);
        Assert.Contains(nameof(VaultOptions.MaxEntries), failure.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, KdbxFailure> MalformedDocuments => new()
    {
        { "duplicate-entry-uuid", KdbxFailure.Corrupt },
        { "entry-without-uuid", KdbxFailure.Corrupt },
        { "invalid-base64", KdbxFailure.Corrupt },
        { "invalid-uuid-length", KdbxFailure.Corrupt },
        { "invalid-time", KdbxFailure.Corrupt },
        { "negative-time", KdbxFailure.Corrupt },
        { "no-root", KdbxFailure.Corrupt },
        { "two-root-groups", KdbxFailure.Corrupt },
        { "wrong-document-element", KdbxFailure.Corrupt },
        { "child-element-in-value", KdbxFailure.Corrupt },
        { "unclosed", KdbxFailure.Corrupt },
        { "not-xml", KdbxFailure.Corrupt },
    };

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public void Malformed_documents_fail_closed(string kind, KdbxFailure expected)
    {
        Func<string, string> mutation = kind switch
        {
            "duplicate-entry-uuid" => xml => DuplicateEntry(xml),
            "entry-without-uuid" => xml => RemoveEntryUuid(xml),
            "invalid-base64" => xml => ReplaceFirstProtected(xml, "***not base64***"),
            "invalid-uuid-length" => xml => ReplaceGroupUuid(xml, "AAAA"),
            "invalid-time" => xml => ReplaceEntryTime(xml, "yesterday"),
            "negative-time" => xml => ReplaceEntryTime(xml, Convert.ToBase64String(BitConverter.GetBytes(-1L))),
            "no-root" => xml => xml[..xml.IndexOf("<Root>", StringComparison.Ordinal)] + "</KeePassFile>",
            "two-root-groups" => xml => xml.Replace("<DeletedObjects", "<Group><UUID>AAAAAAAAAAAAAAAAAAAAAA==</UUID></Group><DeletedObjects"),
            "wrong-document-element" => xml => xml.Replace("<KeePassFile>", "<Other>").Replace("</KeePassFile>", "</Other>"),
            "child-element-in-value" => xml => ReplaceFirst(xml, "<Key>", "</Key>", "<b>Title</b>"),
            "unclosed" => xml => xml[..(xml.Length / 2)],
            _ => _ => "definitely not xml <<<",
        };
        Assert.Equal(expected, FailsWith(Plain, mutation).Failure);
    }

    public static TheoryData<string, KdbxFailure> MalformedInnerHeaders => new()
    {
        { "binary-length-max", KdbxFailure.Corrupt },
        { "binary-length-negative", KdbxFailure.Corrupt },
        { "salsa20-stream", KdbxFailure.Unsupported },
        { "missing-stream-key", KdbxFailure.Corrupt },
        { "oversized-field", KdbxFailure.LimitExceeded },
        { "truncated", KdbxFailure.Corrupt },
    };

    [Theory]
    [MemberData(nameof(MalformedInnerHeaders))]
    public void Malformed_inner_headers_fail_closed(string kind, KdbxFailure expected)
    {
        var file = Read(Compressed);
        var (inner, xml) = Split(OpenPayload(file, Password(Compressed)));
        byte[] payload = kind switch
        {
            "binary-length-max" => [3, .. BitConverter.GetBytes(int.MaxValue), .. inner],
            "binary-length-negative" => [3, .. BitConverter.GetBytes(-5), .. inner],
            "salsa20-stream" => Join(WithStreamId(inner, 2), xml),
            "missing-stream-key" => Join([1, 4, 0, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0], xml),
            "oversized-field" => [9, .. BitConverter.GetBytes(20_000), .. new byte[20_000], .. inner],
            _ => inner[..7],
        };
        if (kind is "binary-length-max" or "binary-length-negative" or "oversized-field")
        {
            payload = [.. payload, .. Encoding.UTF8.GetBytes(xml)];
        }
        var error = Record.Exception(() => Reader().Read(Reseal(file, Password(Compressed), payload), Password(Compressed), false, CancellationToken.None));
        Assert.Equal(expected, Assert.IsType<KdbxException>(error).Failure);
    }

    [Theory]
    [InlineData(Plain)]
    [InlineData(Compressed)]
    public void Random_payload_mutations_either_parse_or_fail_as_kdbx_exception(string name)
    {
        var file = Read(name);
        var original = OpenPayload(file, Password(name));
        var random = new Random(20260926);
        for (var i = 0; i < 150; i++)
        {
            var payload = (byte[])original.Clone();
            switch (i % 3)
            {
                case 0:
                    payload[random.Next(payload.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                    payload = payload[..random.Next(payload.Length)];
                    break;
                default:
                    payload[random.Next(payload.Length)] = (byte)"<>&\"=/ \0"[random.Next(8)];
                    break;
            }
            var mutated = Reseal(file, Password(name), payload);
            var error = Record.Exception(() => Reader().Read(mutated, Password(name), false, CancellationToken.None).Dispose());
            Assert.True(error is null or KdbxException { InnerException: null }, $"{name} mutation {i}: {error?.GetType().Name}");
        }
    }

    private static KdbxException FailsWith(string name, Func<string, string> mutation) =>
        ReadFails(Mutate(name, mutation), Password(name));

    private static byte[] Mutate(string name, Func<string, string> mutation)
    {
        var file = Read(name);
        var (inner, xml) = Split(OpenPayload(file, Password(name)));
        var changed = mutation(xml);
        Assert.NotEqual(xml, changed);
        return Reseal(file, Password(name), Join(inner, changed));
    }

    private static string InsertAfterDeclaration(string xml, string text)
    {
        var end = xml.IndexOf("?>", StringComparison.Ordinal) + 2;
        return xml[..end] + text + xml[end..];
    }

    private static string ReplaceFirst(string xml, string start, string end, string value)
    {
        var from = xml.IndexOf(start, StringComparison.Ordinal) + start.Length;
        var to = xml.IndexOf(end, from, StringComparison.Ordinal);
        return xml[..from] + value + xml[to..];
    }

    // Group times are skipped; only an entry's modification time is interpreted.
    private static string ReplaceEntryTime(string xml, string value)
    {
        var entry = xml.IndexOf("<Entry>", StringComparison.Ordinal);
        return xml[..entry] + ReplaceFirst(xml[entry..], "<LastModificationTime>", "</LastModificationTime>", value);
    }

    private static string ReplaceFirstProtected(string xml, string value) =>
        ReplaceFirst(xml, "<Value Protected=\"True\">", "</Value>", value);

    private static string ReplaceGroupUuid(string xml, string value)
    {
        var group = xml.IndexOf("<Group>", StringComparison.Ordinal);
        return xml[..group] + ReplaceFirst(xml[group..], "<UUID>", "</UUID>", value);
    }

    private static string DuplicateEntry(string xml)
    {
        var start = xml.IndexOf("<Entry>", StringComparison.Ordinal);
        var end = xml.IndexOf("</Entry>", StringComparison.Ordinal) + "</Entry>".Length;
        // History entries share the UUID legitimately; strip them from the copy's source first.
        var entry = xml[start..end];
        var history = entry.IndexOf("<History>", StringComparison.Ordinal);
        if (history >= 0)
        {
            entry = entry[..history] + entry[(entry.IndexOf("</History>", StringComparison.Ordinal) + "</History>".Length)..];
        }
        return xml[..end] + entry.Replace("Protected=\"True\"", "") + xml[end..];
    }

    private static string RemoveEntryUuid(string xml)
    {
        var entry = xml.IndexOf("<Entry>", StringComparison.Ordinal);
        var from = xml.IndexOf("<UUID>", entry, StringComparison.Ordinal);
        var to = xml.IndexOf("</UUID>", from, StringComparison.Ordinal) + "</UUID>".Length;
        return xml[..from] + xml[to..];
    }

    private static byte[] WithStreamId(byte[] inner, uint id)
    {
        var copy = (byte[])inner.Clone();
        var position = 0;
        while (copy[position] != 1)
        {
            position += 5 + BinaryPrimitives.ReadInt32LittleEndian(copy.AsSpan(position + 1));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(position + 5), id);
        return copy;
    }
}
