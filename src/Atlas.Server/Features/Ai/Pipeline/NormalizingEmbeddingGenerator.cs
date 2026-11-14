using Microsoft.Extensions.AI;
using Atlas.Server.Features.Ai.Providers;

namespace Atlas.Server.Features.Ai.Pipeline;

/// <summary>
/// Every vector leaves the pipeline L2-normalized, with the profile's dimensions (1 024, <c>vector(1024)</c> in the index) and
/// only finite values, one per input text; anything else is <see cref="AiInvalidOutputException"/>. With
/// <c>RequestDimensions</c> an OpenAI model that natively returns more is asked for the profile's dimensions.
/// </summary>
public sealed class NormalizingEmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> inner, AiProfileSnapshot profile)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var texts = values as IReadOnlyCollection<string> ?? values.ToArray();
        var dimensions = profile.Dimensions ?? throw new AiConfigurationException("The embeddings profile has no dimensions.");
        if (profile.RequestDimensions && options?.Dimensions is null)
        {
            options = options?.Clone() ?? new EmbeddingGenerationOptions();
            options.Dimensions = dimensions;
        }
        var generated = await base.GenerateAsync(texts, options, cancellationToken);
        if (generated.Count != texts.Count)
        {
            throw new AiInvalidOutputException();
        }
        var result = new GeneratedEmbeddings<Embedding<float>>(generated.Select(embedding => new Embedding<float>(Normalize(embedding.Vector.Span, dimensions))
        {
            ModelId = embedding.ModelId,
            CreatedAt = embedding.CreatedAt,
        }))
        {
            Usage = generated.Usage,
        };
        return result;
    }

    /// <exception cref="AiInvalidOutputException">Another length, a non-finite value, or the zero vector.</exception>
    public static float[] Normalize(ReadOnlySpan<float> vector, int dimensions)
    {
        if (vector.Length != dimensions)
        {
            throw new AiInvalidOutputException();
        }
        double sum = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                throw new AiInvalidOutputException();
            }
            sum += (double)value * value;
        }
        var norm = Math.Sqrt(sum);
        if (norm == 0 || !double.IsFinite(norm))
        {
            throw new AiInvalidOutputException();
        }
        var normalized = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
        {
            normalized[i] = (float)(vector[i] / norm);
        }
        return normalized;
    }
}
