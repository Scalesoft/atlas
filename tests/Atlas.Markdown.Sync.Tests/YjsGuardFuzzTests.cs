using System.Text.Json;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// Concurrent sessions of the real editor (packages/markdown/src/collab/yjsGuardFuzz.test.ts): several clients edit at once,
/// their outboxes merge pending updates and the relay broadcasts late. The guard must admit every update a real editor sends
/// (a refusal stops that user's session) and its replica must hold the document real Yjs holds after each one.
/// Fixtures/yjs-fuzz keeps regression traces; ATLAS_YJS_FUZZ names a directory of freshly generated ones.
/// </summary>
public sealed class YjsGuardFuzzTests
{
    private sealed record Step(int Client, string Update, string Hash, string[] Actions);
    private sealed record Trace(int Seed, string Baseline, Step[] Steps, string? Final);

    public static TheoryData<string> Traces
    {
        get
        {
            var directories = new[] { Path.Combine(AppContext.BaseDirectory, "Fixtures", "yjs-fuzz"), Environment.GetEnvironmentVariable("ATLAS_YJS_FUZZ") };
            var files = directories.Where(directory => directory is { Length: > 0 } && Directory.Exists(directory))
                .SelectMany(directory => Directory.GetFiles(directory!, "*.json")).Order(StringComparer.Ordinal).ToArray();
            return files.Length > 0 ? new(files) : new(["none"]);
        }
    }

    [Theory]
    [MemberData(nameof(Traces))]
    public void Every_update_of_concurrent_real_editors_is_admitted_and_followed(string file)
    {
        if (file == "none") return;
        var trace = JsonSerializer.Deserialize<Trace>(File.ReadAllText(file), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var guard = YjsCorpus.Guard();
        var (seeded, index) = guard.Seed(Convert.FromBase64String(trace.Baseline));
        Assert.True(seeded.IsAccepted, $"seed {trace.Seed}: baseline {seeded.Reason} {seeded.Detail}");
        var refused = new List<string>();
        for (var step = 0; step < trace.Steps.Length; step++)
        {
            var current = trace.Steps[step];
            var update = Convert.FromBase64String(current.Update);
            var verdict = guard.Admit(index!, update);
            if (!verdict.IsAccepted)
            {
                // Keep following the relay, so one refusal does not hide a later divergence.
                refused.Add($"step {step} (client {current.Client}, after {string.Join(", ", current.Actions)}): {verdict.Reason} {verdict.Detail}");
                index!.ApplyTrusted(update, 4L * 1024 * 1024);
            }
            Assert.True(current.Hash == YjsDocumentTreeTests.Hash(index!.Tree!),
                $"seed {trace.Seed} step {step}: the replica differs from Yjs, it holds\n{YjsDocumentTreeTests.Canonical(index.Tree!)}");
            // The provider resends an update whose acknowledgement it lost (with a new id): Yjs ignores what it already has.
            var resent = guard.Admit(index, update);
            if (!resent.IsAccepted) refused.Add($"step {step} resent: {resent.Reason} {resent.Detail}");
            Assert.True(current.Hash == YjsDocumentTreeTests.Hash(index.Tree!), $"seed {trace.Seed} step {step}: resending changed the replica");
        }
        if (trace.Final is { } final && trace.Steps.Length > 0)
        {
            // The snapshotter pushes the merged document as one state.
            var snapshot = guard.Snapshot(index!, Convert.FromBase64String(final));
            if (!snapshot.IsAccepted) refused.Add($"final snapshot: {snapshot.Reason} {snapshot.Detail}");
            Assert.Equal(trace.Steps[^1].Hash, YjsDocumentTreeTests.Hash(YjsCorpus.Index(Convert.FromBase64String(final)).Tree!));
        }
        Assert.True(refused.Count == 0, $"seed {trace.Seed}: {refused.Count} of {trace.Steps.Length} real updates refused:\n{string.Join("\n", refused)}");
    }
}
