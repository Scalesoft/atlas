using System.Globalization;
using System.Text;
using Atlas.Server.Features.Ai.Providers;

namespace Atlas.Server.Features.AiIndex.Domain;

public static class IndexGenerationState
{
    public const string Building = "Building";
    public const string Active = "Active";
    public const string Retired = "Retired";
    public const string Failed = "Failed";
}

/// <summary>One row of <c>IndexGeneration</c>.</summary>
public sealed record IndexGenerationRow(Guid Id, string State, Guid ProfileRevisionId, string Fingerprint, string ChunkingVersion,
    int Dimensions, DateTime CreatedAt, DateTime BoundaryAt, DateTime? ActivatedAt, DateTime? EndedAt, DateTime Version);

/// <summary>What the worker embeds for one claimed task, read in the claim transaction: the page's current published state and
/// the hashes of what the generation already holds for it. <see cref="Token"/> is the task's EnqueuedAt; the write compares it.</summary>
public sealed record IndexSource(Guid GenerationId, Guid PageId, DateTime Token, AiProfileSnapshot Profile, Guid RevisionId, bool IsTemplate,
    string Title, string Markdown, IReadOnlyList<IndexHeading> Headings, IReadOnlySet<string> ExistingChunkHashes, string? ExistingTitleHash);

/// <summary>A chunk to store; <see cref="Vector"/> null keeps the vector the generation already holds for the same hash.</summary>
public sealed record IndexedChunk(int Ordinal, string? HeadingId, string? HeadingTitle, string Text, byte[] TextHash, string? Vector);

/// <summary>pgvector's text form, so the index needs no Npgsql plugin: <c>CAST(:v AS vector)</c> from "[f1,f2,…]" in the
/// invariant culture.</summary>
public static class VectorText
{
    public static string Format(ReadOnlySpan<float> vector)
    {
        var text = new StringBuilder(vector.Length * 12 + 2).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0)
            {
                text.Append(',');
            }
            text.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    public static float[] Parse(string text)
    {
        var body = text.AsSpan().Trim().TrimStart('[').TrimEnd(']');
        var values = new List<float>(1024);
        foreach (var range in body.Split(','))
        {
            values.Add(float.Parse(body[range], NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        return values.ToArray();
    }

    public static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}

/// <summary>Exponential backoff of a task that failed for a reason other than an outage: 10 s, 20 s, 40 s … at most 15 minutes,
/// ±10 % so failing tasks do not retry in lockstep.</summary>
public static class IndexBackoff
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Max = TimeSpan.FromMinutes(15);

    /// <param name="attempts">Failed attempts before this one.</param>
    /// <param name="jitter">A factor in [0.9, 1.1].</param>
    public static TimeSpan Delay(int attempts, double jitter)
    {
        var seconds = First.TotalSeconds * Math.Pow(2, Math.Clamp(attempts, 0, 20)) * jitter;
        return TimeSpan.FromSeconds(Math.Min(Max.TotalSeconds, seconds));
    }

    public static double Jitter() => 0.9 + Random.Shared.NextDouble() * 0.2;
}
