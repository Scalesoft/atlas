using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Atlas.Markdown.Sync;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>A meter listener sees every <see cref="MarkdownSyncMetrics"/> instance of the process by its name, so
/// this collection runs alone: no other test records a flush or holds documents while it listens.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MarkdownSyncMetricsCollection
{
    public const string Name = "Markdown sync metrics";
}

/// <summary>ADR-018 A-M5 — the collab relay's OTel metrics (gauges + flush histogram) publish
/// under the documented meter name and observe the live registry. Locks the metric names.</summary>
[Collection(MarkdownSyncMetricsCollection.Name)]
public class MarkdownSyncMetricsTests
{
    [Fact]
    public void Metrics_publish_gauges_and_flush_histogram_under_the_meter_name()
    {
        var registry = new DocumentRegistry(new InMemoryDocumentStore(), new MarkdownSyncOptions(), new CollaboratorColors(), NullLogger<DocumentRegistry>.Instance);
        using var metrics = new MarkdownSyncMetrics(registry);

        // Every measurement per instrument: an instance another test left alive reports under the same name too.
        var observed = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        void Record(Instrument inst, double value)
        {
            lock (observed)
            {
                if (!observed.TryGetValue(inst.Name, out var values)) observed[inst.Name] = values = [];
                values.Add(value);
            }
        }
        using var listener = new MeterListener
        {
            InstrumentPublished = (inst, l) =>
            {
                if (inst.Meter.Name == MarkdownSyncMetrics.MeterName) l.EnableMeasurementEvents(inst);
            },
        };
        listener.SetMeasurementEventCallback<int>((inst, val, _, _) => Record(inst, val));
        listener.SetMeasurementEventCallback<long>((inst, val, _, _) => Record(inst, val));
        listener.SetMeasurementEventCallback<double>((inst, val, _, _) => Record(inst, val));
        listener.Start();

        // Histogram: recorded on demand.
        metrics.RecordFlush(12.5, ok: true);
        // Gauges: pulled on demand.
        listener.RecordObservableInstruments();

        Assert.Contains("tiptap.documents.active", observed.Keys);
        Assert.Contains("tiptap.documents.resident", observed.Keys);
        Assert.Contains("tiptap.connections.active", observed.Keys);
        Assert.Contains("tiptap.flush.duration", observed.Keys);
        Assert.Contains(12.5, observed["tiptap.flush.duration"]);
        Assert.Contains(0, observed["tiptap.documents.active"]); // no docs yet
    }
}
