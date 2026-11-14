using System.Diagnostics.Metrics;

namespace Atlas.Markdown.Sync;

/// <summary>
/// OpenTelemetry metrics for the collab relay (ADR-018 A-M5). Self-contained: the library owns
/// its own <see cref="Meter"/> so it stays packable with zero host coupling; the host opts in by
/// adding <see cref="MeterName"/> to its OTel meter provider (gated on the OTLP endpoint, so a
/// plain run pays no observability tax — the meter is a no-op without a listener). Never records
/// document content — only counts and timings.
/// </summary>
public sealed class MarkdownSyncMetrics : IDisposable
{
    /// <summary>Meter name the host registers with <c>AddMeter(...)</c> on its OTel pipeline.</summary>
    public const string MeterName = "Atlas.Markdown.Sync";

    private readonly Meter _meter;
    private readonly Histogram<double> _flushDuration;

    public MarkdownSyncMetrics(DocumentRegistry registry)
    {
        _meter = new Meter(MeterName);

        _meter.CreateObservableGauge(
            "tiptap.documents.active",
            () => registry.ActiveDocumentCount,
            unit: "{document}",
            description: "Documents with at least one connected collaborator.");

        _meter.CreateObservableGauge(
            "tiptap.documents.resident",
            () => registry.Count,
            unit: "{document}",
            description: "Documents held in memory, including warm-kept zero-peer docs awaiting eviction.");

        _meter.CreateObservableGauge(
            "tiptap.connections.active",
            () => registry.TotalPeerCount,
            unit: "{connection}",
            description: "Total connected collaborators across all documents (sum of per-doc peers).");

        _flushDuration = _meter.CreateHistogram<double>(
            "tiptap.flush.duration",
            unit: "ms",
            description: "Wall-clock duration of a per-document flush tick (log append + snapshot write), tagged outcome=ok|error.");
    }

    /// <summary>Record a flush tick's wall-clock duration and whether the store write succeeded.</summary>
    public void RecordFlush(double milliseconds, bool ok) =>
        _flushDuration.Record(milliseconds, new KeyValuePair<string, object?>("outcome", ok ? "ok" : "error"));

    public void Dispose() => _meter.Dispose();
}
