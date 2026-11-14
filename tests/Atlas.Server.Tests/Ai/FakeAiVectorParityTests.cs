using System.Text.Json;
using Atlas.Server.Tests.Ai.Fakes;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>
/// The fake embedding exists twice: <see cref="FakeAi"/> for the in-process suites and tests/browser/fake-ai/vectors.mjs for
/// the HTTP fake server of the browser scenarios. tests/fixtures/fake-ai-vectors.json holds the numbers; this test and
/// tests/browser/fake-ai/server.test.mjs both check their side against it, so the two cannot drift apart unnoticed. Change the
/// algorithm on both sides, regenerate the fixture from the JS side and let this test prove the C# side agrees.
/// </summary>
public sealed class FakeAiVectorParityTests
{
    private sealed record Hash(string Text, uint Fnv);

    private sealed record Entry(string Text, int[][] Counts, double Norm, string Near);

    private sealed record Fixture(int Dimensions, Hash[] Hashes, Entry[] Entries);

    private static Fixture Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "fake-ai-vectors.json");
        return JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    [Fact]
    public void The_C_sharp_fake_and_the_JavaScript_fake_server_produce_identical_trigram_vectors()
    {
        var fixture = Load();
        Assert.Equal(FakeAi.Dimensions, fixture.Dimensions);
        Assert.NotEmpty(fixture.Entries);
        foreach (var entry in fixture.Entries)
        {
            var counts = FakeAi.Counts(entry.Text);
            var expected = new float[FakeAi.Dimensions];
            foreach (var pair in entry.Counts)
            {
                expected[pair[0]] = pair[1];
            }
            // Integer counts: the hash, the word splitting and the trigram windows agree exactly, or the slot differs.
            Assert.True(expected.AsSpan().SequenceEqual(counts), $"raw trigram counts differ for \"{entry.Text}\"");
            Assert.Equal(entry.Near, FakeAi.Near(entry.Text));

            var vector = FakeAi.Vector(entry.Text);
            if (entry.Norm == 0)
            {
                Assert.All(vector, value => Assert.Equal(0f, value));
                continue;
            }
            for (var slot = 0; slot < vector.Length; slot++)
            {
                Assert.Equal(expected[slot] / entry.Norm, vector[slot], 1e-6);
            }
        }
    }

    [Fact]
    public void The_hash_is_FNV_1a_over_UTF_16_code_units()
    {
        foreach (var hash in Load().Hashes)
        {
            var value = 2166136261u;
            foreach (var c in hash.Text)
            {
                value = (value ^ c) * 16777619u;
            }
            Assert.Equal(hash.Fnv, value);
        }
    }
}
