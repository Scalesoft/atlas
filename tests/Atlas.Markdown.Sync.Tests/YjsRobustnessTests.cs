using System.Diagnostics;

namespace Atlas.Markdown.Sync.Tests;

public class YjsRobustnessTests
{
    private const long MaxDocumentBytes = 4L * 1024 * 1024;

    [Fact]
    public void Seeded_fuzz_only_ever_produces_a_verdict_within_its_time_budget()
    {
        var guard = YjsCorpus.Guard();
        var baseline = YjsCorpus.Read("perf/50k/state.bin");
        var seeds = Directory.GetFiles(Path.Combine(YjsCorpus.Root, "perf", "50k"), "u*.bin")
            .Concat(Directory.GetFiles(Path.Combine(YjsCorpus.Root, "editing"), "*.update.bin"))
            .Concat(Directory.GetFiles(Path.Combine(YjsCorpus.Root, "hostile"), "*.update.bin"))
            .Select(File.ReadAllBytes).ToArray();
        var random = new Random(20260926);
        var index = YjsCorpus.Index(baseline);
        var budget = Stopwatch.StartNew();
        var slowest = TimeSpan.Zero;
        int accepted = 0, rejected = 0;
        for (var iteration = 0; iteration < 5000; iteration++)
        {
            var input = Mutate(random, seeds);
            var started = Stopwatch.GetTimestamp();
            var verdict = guard.Admit(index, input); // anything but a verdict fails the test
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed > slowest) slowest = elapsed;
            if (verdict.IsAccepted && ++accepted % 100 == 0) index = YjsCorpus.Index(baseline);
            else if (!verdict.IsAccepted) rejected++;
        }
        Assert.True(budget.Elapsed < TimeSpan.FromSeconds(30), $"fuzz took {budget.Elapsed}");
        Assert.True(slowest < TimeSpan.FromSeconds(1), $"slowest input took {slowest}");
        Assert.True(accepted > 0 && rejected > 0, $"accepted {accepted}, rejected {rejected}");
    }

    public static TheoryData<string> PerfSizes => new(new[] { "50k", "256k", "1m", "4m" }
        .Where(size => File.Exists(Path.Combine(YjsCorpus.Root, "perf", size, "state.bin"))));

    [Theory]
    [MemberData(nameof(PerfSizes))]
    public void Incremental_admission_equals_an_index_rebuilt_from_state_and_log(string size)
    {
        var (state, updates) = Perf(size);
        var guard = YjsCorpus.Guard(maxDocumentBytes: 8 * MaxDocumentBytes);
        var (seed, index) = guard.Seed(state);
        Assert.True(seed.IsAccepted, seed.Detail);
        foreach (var update in updates) Assert.True(guard.Admit(index!, update).IsAccepted);
        var rebuilt = YjsDocumentIndex.Build(state, updates.Select(update => (ReadOnlyMemory<byte>)update), 8 * MaxDocumentBytes, YjsCorpus.Reserved);
        Assert.True(index!.IsEquivalentTo(rebuilt));
        Assert.True(guard.Snapshot(index, state).IsAccepted);
    }

    [Fact]
    public void Corpus_scenarios_index_the_same_incrementally_and_rebuilt()
    {
        var guard = YjsCorpus.Guard();
        foreach (var scenario in YjsCorpus.Manifest.Where(entry => entry.Scenario.StartsWith("node-", StringComparison.Ordinal) || entry.Scenario == "editing")
                     .GroupBy(entry => entry.Scenario))
        {
            var entries = scenario.OrderBy(entry => entry.Seq).ToArray();
            var baseline = YjsCorpus.Read(entries[0].File);
            var accepted = entries.Where(entry => !entry.IsState && YjsCorpus.ExpectAccept(entry)).Select(entry => YjsCorpus.Read(entry.File)).ToArray();
            if (entries.Any(entry => !entry.IsState && !YjsCorpus.ExpectAccept(entry))) continue; // rejected steps break the chain
            var incremental = YjsCorpus.Index(baseline);
            foreach (var update in accepted) Assert.True(guard.Admit(incremental, update).IsAccepted, scenario.Key);
            var rebuilt = YjsDocumentIndex.Build(baseline, accepted.Select(update => (ReadOnlyMemory<byte>)update), MaxDocumentBytes, YjsCorpus.Reserved);
            Assert.True(incremental.IsEquivalentTo(rebuilt), scenario.Key);
        }
    }

    /// <summary>
    /// Instant typing on a 1 MiB document (25,000 replica items), full document validation included: an update reads the elements
    /// it changed, not the document. A quarter of these random edits add or remove a top-level block, which re-checks the
    /// document's content expression over the top-level list (read twice: about 4,000 items); any other edit reads under 100.
    /// The budget is that count, because wall-clock time depends on the machine (p95 0.35 ms on a developer machine, 1.8 ms on
    /// the CI runner).
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void Admitting_an_update_to_a_1_MiB_document_reads_the_elements_it_changed_not_the_document()
    {
        var (state, updates) = Perf("1m");
        var guard = YjsCorpus.Guard();
        var timings = new List<double>();
        for (var round = 0; round < 4; round++)
        {
            var (_, index) = guard.Seed(state);
            foreach (var update in updates)
            {
                var read = YjsTreeValidation.ItemsRead; // the guard validates synchronously, on this thread
                var started = Stopwatch.GetTimestamp();
                Assert.True(guard.Admit(index!, update).IsAccepted);
                if (round > 0) timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                read = YjsTreeValidation.ItemsRead - read;
                var topLevel = 0;
                for (var item = index!.Tree!.Root("default")!.Start; item is not null; item = item.Right) topLevel++;
                Assert.True(read <= 2 * topLevel + 256, $"read {read} of {index.Tree.ItemCount} items, {topLevel} top-level");
            }
        }
        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(0.95 * timings.Count) - 1];
        // A smoke bound for a pathological regression only: the deterministic budget is the count above, the runners vary.
        Assert.True(p95 < 10.0, $"p95 {p95:0.000} ms");
    }

    /// <summary>
    /// Inserts of many clients at one position make YATA pass every earlier one: quadratic, 16 s for 32,000 structs (360 KB)
    /// under the document's gate before the conflict budget (YjsLimits.MaxConflictScan) refused it.
    /// </summary>
    [Fact]
    public void Concurrent_inserts_of_many_clients_at_one_position_are_refused_quickly()
    {
        var baseline = new YUpdateBuilder().Structs(1, 0, YUpdateBuilder.Element(YUpdateBuilder.At.InRoot(), "paragraph"),
            YUpdateBuilder.Text(YUpdateBuilder.At.Under(1, 0)), YUpdateBuilder.Str(YUpdateBuilder.At.Under(1, 1), "hello")).Build();
        var builder = new YUpdateBuilder();
        for (var client = 0; client < 32_000; client++)
            builder.Structs((ulong)(100 + client), 0, YUpdateBuilder.Str(new YUpdateBuilder.At(Origin: (1, 3), Right: (1, 4)), "x"));
        var watch = Stopwatch.StartNew();
        var verdict = YjsCorpus.Guard().Admit(YjsCorpus.Index(baseline), builder.Build());
        // The conflict budget refused it, so YATA passed at most MaxConflictScan items: the count is the bound.
        Assert.Equal(ContentRejectionReason.LimitExceeded, verdict.Reason);
        Assert.Equal("too many concurrent inserts at one position", verdict.Detail);
        // A smoke bound only (1 s on a developer machine under the full suite): the runners vary.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"{watch.Elapsed}");
    }

    private static readonly byte[] Paragraph = new YUpdateBuilder().Structs(1, 0, YUpdateBuilder.Element(YUpdateBuilder.At.InRoot(), "paragraph"),
        YUpdateBuilder.Text(YUpdateBuilder.At.Under(1, 0)), YUpdateBuilder.Str(YUpdateBuilder.At.Under(1, 1), "hello")).Build();

    /// <summary>Typing merges into the run it extends, as Yjs merges items after a transaction: a long session does not grow per keystroke.</summary>
    [Fact]
    public void Typing_one_keystroke_per_update_keeps_one_item()
    {
        var index = YjsCorpus.Index(Paragraph);
        var guard = YjsCorpus.Guard();
        var before = index.Tree!.ItemCount;
        for (uint clock = 0; clock < 2_000; clock++)
        {
            var origin = clock == 0 ? (1UL, 6u) : (2UL, clock - 1);
            Assert.True(guard.Admit(index, new YUpdateBuilder().Structs(2, clock, YUpdateBuilder.Str(new YUpdateBuilder.At(Origin: origin), "x")).Build()).IsAccepted);
        }
        // Deleting a range splits the run and leaves one deleted run; deleting the rest merges it again.
        Assert.True(guard.Admit(index, new YUpdateBuilder().Delete(2, 500, 300).Build()).IsAccepted);
        Assert.True(guard.Admit(index, new YUpdateBuilder().Delete(2, 800, 200).Build()).IsAccepted);
        Assert.Equal(before + 3, index.Tree.ItemCount);
    }

    /// <summary>
    /// A payload of many tiny items no editor writes would hold about 300 bytes of replica per encoded 10 bytes (400,000
    /// items in a 4 MiB update kept 119 MiB): refused beyond YjsLimits.ItemBudget, and the replica stays as it was.
    /// </summary>
    [Fact]
    public void A_payload_of_many_tiny_items_is_refused_beyond_the_item_budget()
    {
        const int count = 200_000;
        var structs = new byte[count][];
        for (var i = 0; i < count; i++)
            structs[i] = i % 2 == 0
                ? YUpdateBuilder.Str(new YUpdateBuilder.At(Origin: i == 0 ? (1UL, 6u) : (2UL, (uint)i - 1)), "x")
                : YUpdateBuilder.Format(new YUpdateBuilder.At(Origin: (2UL, (uint)i - 1)), "bold", i % 4 == 1 ? "{}" : "null");
        var index = YjsCorpus.Index(Paragraph);
        var verdict = YjsCorpus.Guard().Admit(index, new YUpdateBuilder().Structs(2, 0, structs).Build());
        Assert.Equal(ContentRejectionReason.TooManyItems, verdict.Reason);
        Assert.Equal(3, index.Tree!.ItemCount);
        Assert.True(index.IsEquivalentTo(YjsCorpus.Index(Paragraph)));
    }

    private static (byte[] State, byte[][] Updates) Perf(string size)
    {
        var directory = Path.Combine(YjsCorpus.Root, "perf", size);
        return (File.ReadAllBytes(Path.Combine(directory, "state.bin")),
            Directory.GetFiles(directory, "u*.bin").OrderBy(file => file, StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray());
    }

    private static byte[] Mutate(Random random, byte[][] seeds)
    {
        var input = (byte[])seeds[random.Next(seeds.Length)].Clone();
        switch (random.Next(6))
        {
            case 0:
                for (int flip = 0, count = random.Next(1, 9); flip < count && input.Length > 0; flip++)
                    input[random.Next(input.Length)] ^= (byte)(1 << random.Next(8));
                return input;
            case 1:
                return input[..random.Next(0, Math.Max(1, input.Length))];
            case 2:
            {
                var bytes = new byte[random.Next(0, 512)];
                random.NextBytes(bytes);
                return bytes;
            }
            case 3:
            {
                var other = seeds[random.Next(seeds.Length)];
                return [.. input[..random.Next(0, input.Length + 1)], .. other[random.Next(0, other.Length + 1)..]];
            }
            case 4:
            {
                // Huge or overlong varints spliced in at a random position.
                byte[][] huge = [[0xFF, 0xFF, 0xFF, 0xFF, 0x0F], [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01], [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01]];
                var position = random.Next(0, input.Length + 1);
                return [.. input[..position], .. huge[random.Next(huge.Length)], .. input[position..]];
            }
            default:
                for (int set = 0, count = random.Next(1, 5); set < count && input.Length > 0; set++)
                    input[random.Next(input.Length)] = (byte)random.Next(256);
                return input;
        }
    }
}
