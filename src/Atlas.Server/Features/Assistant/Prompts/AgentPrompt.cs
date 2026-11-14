namespace Atlas.Server.Features.Assistant.Prompts;

/// <summary>
/// The versioned system prompt of the agent turn (<c>assistant.agent</c>, spec §4.1), the counterpart of <see cref="AnswerPrompt"/>:
/// each version is an embedded <c>agent.system.vN.txt</c> and a constant stamped on the message as its <c>PromptVersion</c>; an old
/// file stays. A new version means a new file, a new constant, a catalogue entry and a pull request with the result of the agent
/// evaluation (<c>tests/ai-eval/agent/README.md</c>).
/// </summary>
public static class AgentPrompt
{
    public const string V1 = "agent@1";

    /// <summary>WP-23: changes are only proposed, one batch with all pages; icons only from the catalogue; tree first.</summary>
    public const string V2 = "agent@2";

    /// <summary>WP-31: drafts (read first, sections, placeholders back) and "přeformátuj podle X" as one atlas_replace_page.</summary>
    public const string V3 = "agent@3";

    /// <summary>WP-40, WP-41: moves from the tree, one page per call; publication only on an explicit request in the last message,
    /// never for page text; a draft first, its publication after the user confirmed it.</summary>
    public const string V4 = "agent@4";

    /// <summary>The turn starts with the results of a server-side atlas_search of the question (passages included): answer from
    /// them when they suffice, search again with other words or read a page only when they do not.</summary>
    public const string V5 = "agent@5";

    /// <summary>The left menu of a space as one atlas_arrange_pages proposal (all moves, hubs first, siblings top down) instead of one
    /// move per confirmation.</summary>
    public const string V6 = "agent@6";

    /// <summary>Plan 3.5 I: questions about requirement pages (waiting, blocked, missing, parties, modules) go to projector_query /
    /// projector_waiting with the words of the page or requirement, not to full text; own state words and history stay in the text.</summary>
    public const string V7 = "agent@7";

    /// <summary>The version new agent turns are produced with and stamped on messages.</summary>
    public const string Version = V7;

    /// <summary>Every shipped version and its embedded resource name.</summary>
    public static IReadOnlyDictionary<string, string> Resources { get; } = new Dictionary<string, string>
    {
        [V1] = "Atlas.Server.Features.Assistant.Prompts.agent.system.v1.txt",
        [V2] = "Atlas.Server.Features.Assistant.Prompts.agent.system.v2.txt",
        [V3] = "Atlas.Server.Features.Assistant.Prompts.agent.system.v3.txt",
        [V4] = "Atlas.Server.Features.Assistant.Prompts.agent.system.v4.txt",
        [V5] = "Atlas.Server.Features.Assistant.Prompts.agent.system.v5.txt",
        [V6] = "Atlas.Server.Features.Assistant.Prompts.agent.system.v6.txt",
        [V7] = "Atlas.Server.Features.Assistant.Prompts.agent.system.v7.txt",
    };

    /// <summary>The system text of the current version.</summary>
    public static string System => SystemFor(Version);

    /// <summary>The system text of a stored <c>PromptVersion</c>; throws for an unknown version.</summary>
    public static string SystemFor(string version)
    {
        if (!Resources.TryGetValue(version, out var resource))
        {
            throw new ArgumentException($"Unknown agent prompt version '{version}'.", nameof(version));
        }
        using var stream = typeof(AgentPrompt).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded prompt resource '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings("\n").TrimEnd();
    }
}
