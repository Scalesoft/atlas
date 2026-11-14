namespace Atlas.Markdown.Sync.Tests;

/// <summary>Verdicts of the managed content guard on the real-editor corpus, against the manifest of the observer oracle.</summary>
public class YjsCorpusTests
{
    // The observer oracle never sees content a payload inserts and deletes again; the structural scan does.
    private static readonly HashSet<string> StricterThanOracle = new(StringComparer.Ordinal)
    {
        "hostile/012-secret_inserted_and_deleted_in_the_same_update_smuggle_.update.bin",
        "hostile/013-unknown_node_inserted_and_deleted_in_the_same_update_smuggle_.update.bin",
    };

    public static TheoryData<string> Scenarios => new(YjsCorpus.Manifest.Select(entry => entry.Scenario).Distinct());

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Every_manifest_verdict_matches(string scenario)
    {
        var guard = YjsCorpus.Guard();
        var entries = YjsCorpus.Manifest.Where(entry => entry.Scenario == scenario).OrderBy(entry => entry.Seq).ToArray();
        Assert.NotEmpty(entries);
        var baseline = YjsCorpus.Read(entries[0].File);
        Assert.True(entries[0].IsState);
        // Replays every step, rejected ones included, so later steps see the state the oracle saw.
        var running = YjsCorpus.Index(baseline);
        foreach (var entry in entries)
        {
            var payload = YjsCorpus.Read(entry.File);
            if (entry.IsState)
            {
                // States are what joining peers load: they must pass on their own.
                Verify(entry, guard.Seed(payload).Admission);
                continue;
            }
            // Hostile cases are independent diffs against the baseline.
            var index = scenario == "hostile" ? YjsCorpus.Index(baseline) : running;
            var verdict = guard.Admit(index, payload);
            Verify(entry, verdict);
            if (scenario != "hostile" && !verdict.IsAccepted) running.ApplyTrusted(payload, 4L * 1024 * 1024);
        }
    }

    [Fact]
    public void A_snapshot_with_clocks_that_were_never_admitted_is_refused()
    {
        var guard = YjsCorpus.Guard();
        var baseline = YjsCorpus.Index(YjsCorpus.Read("node-heading/000-baseline.state.bin"));
        var final = YjsCorpus.Read("node-heading/007-final.state.bin");
        Assert.True(guard.Seed(final).Admission.IsAccepted);
        Assert.Equal(ContentRejectionReason.SnapshotExceedsState, guard.Snapshot(baseline, final).Reason);
        Assert.True(guard.Snapshot(baseline, YjsCorpus.Read("node-heading/000-baseline.state.bin")).IsAccepted);
    }

    public static TheoryData<string> RoundtripSizes => new(Directory.GetFiles(Path.Combine(YjsCorpus.Root, "roundtrip"), "*-yrs-state.bin")
        .Select(file => Path.GetFileName(file).Split('-')[0]));

    [Theory]
    [MemberData(nameof(RoundtripSizes))]
    public void States_re_encoded_by_yrs_are_admitted_with_the_same_state_vector(string size)
    {
        var (admission, index) = YjsCorpus.Guard().Seed(YjsCorpus.Read($"roundtrip/{size}-yrs-state.bin"));
        Assert.True(admission.IsAccepted, admission.Detail);
        var reader = new Lib0Reader(YjsCorpus.Read($"roundtrip/{size}-yrs-sv.bin"));
        var expected = new Dictionary<ulong, uint>();
        for (var count = reader.ReadCount(); count > 0; count--) expected[reader.ReadVarUint()] = reader.ReadClock();
        Assert.Equal(expected.OrderBy(pair => pair.Key), index!.StateVector.OrderBy(pair => pair.Key));
    }

    [Fact]
    public void Inputs_that_crashed_native_yrs_only_ever_produce_a_verdict()
    {
        var guard = YjsCorpus.Guard();
        var baseline = YjsCorpus.Read("perf/50k/state.bin");
        var files = Directory.GetFiles(Path.Combine(YjsCorpus.Root, "yrs-crashes"), "*.bin");
        Assert.NotEmpty(files);
        foreach (var file in files)
            _ = guard.Admit(YjsCorpus.Index(baseline), File.ReadAllBytes(file)); // any exception fails the test
    }

    private static void Verify(CorpusEntry entry, ContentAdmission verdict)
    {
        if (YjsCorpus.RefusedValues.TryGetValue(entry.File, out var refused))
        {
            Assert.True(verdict.Reason == refused, $"{entry.File}: expected {refused}, got {verdict.Reason} {verdict.Detail}");
            return;
        }
        var expectAccept = entry.ExpectAccept && !StricterThanOracle.Contains(entry.File);
        Assert.True(verdict.IsAccepted == expectAccept,
            $"{entry.File}: expected {(expectAccept ? "accept" : "reject " + entry.Reason)}, got {verdict.Reason} {verdict.Detail}");
        if (!verdict.IsAccepted && entry.Reason.Length > 0)
            Assert.Contains(verdict.Reason!.Value, YjsCorpus.ReasonsFor(entry.Reason));
        if (StricterThanOracle.Contains(entry.File))
            Assert.Contains(verdict.Reason!.Value, new[] { ContentRejectionReason.ReservedNode, ContentRejectionReason.UnknownNode });
    }
}
