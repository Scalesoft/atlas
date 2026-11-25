namespace Atlas.Server.Features.Assistant.Prompts;

/// <summary>
/// The versioned system prompt of the answer flow (plan phase 3, Q7). A prompt is code: each version is an embedded
/// <c>answer.system.vN.txt</c> and a constant, an old file stays because stored messages carry their <c>PromptVersion</c>.
/// A new version means a new file, a new constant, a catalogue entry and a pull request with the evaluation result
/// (<c>tests/ai-eval/answer/README.md</c>). The prototype links this file, so it measures exactly the production prompt.
/// </summary>
public static class AnswerPrompt
{
    public const string V1 = "answer@1";
    public const string V2 = "answer@2";
    /// <summary>Answers from partly matching sources and says what they lack; links names and abbreviations to sources.</summary>
    public const string V3 = "answer@3";
    /// <summary>A quick overview: one short lead sentence, then short single-fact points with one key value in bold.</summary>
    public const string V4 = "answer@4";

    /// <summary>The version new answers are produced with and stamped on messages.</summary>
    public const string Version = V4;

    /// <summary>Every shipped version and its embedded resource name.</summary>
    public static IReadOnlyDictionary<string, string> Resources { get; } = new Dictionary<string, string>
    {
        [V1] = "Atlas.Server.Features.Assistant.Prompts.answer.system.v1.txt",
        [V2] = "Atlas.Server.Features.Assistant.Prompts.answer.system.v2.txt",
        [V3] = "Atlas.Server.Features.Assistant.Prompts.answer.system.v3.txt",
        [V4] = "Atlas.Server.Features.Assistant.Prompts.answer.system.v4.txt",
    };

    /// <summary>The system text of the current version.</summary>
    public static string System => SystemFor(Version);

    /// <summary>The system text of a stored <c>PromptVersion</c>; throws for an unknown version.</summary>
    public static string SystemFor(string version)
    {
        if (!Resources.TryGetValue(version, out var resource))
        {
            throw new ArgumentException($"Unknown answer prompt version '{version}'.", nameof(version));
        }
        using var stream = typeof(AnswerPrompt).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
}
