using System.ComponentModel;

namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>Structured model output of the answer flow (plan §7); the Czech descriptions go into the JSON schema.</summary>
[Description("Odpověď na otázku pouze z očíslovaných zdrojů.")]
public sealed record AnswerResult(
    [property: Description("False, pokud zdroje odpověď neobsahují.")] bool Answerable,
    IReadOnlyList<AnswerSentence> Sentences);

public sealed record AnswerSentence(
    [property: Description("Jedna věta odpovědi česky, bez čísel zdrojů.")] string Text,
    [property: Description("Čísla zdrojů, ze kterých věta vychází.")] IReadOnlyList<int> Sources);
