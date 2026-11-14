namespace Atlas.Server.Features.Assistant.Answering;

/// <summary>
/// How much of the wiki one answer reads (the assistant's „Náročnost“ meter, five steps): <see cref="Take"/> sources in all, at most
/// <see cref="PerPage"/> chunks of a page outside a focused short-section window, out of <see cref="Pages"/> pages considered.
/// Quick is the original R3 limit; a deeper answer gives the model more text to summarize and costs more input tokens
/// (at most 14 sources of 2 600 characters, about 12 000 tokens).
/// </summary>
public sealed record AnswerEffort(string Name, int Take, int PerPage, int Pages)
{
    public static readonly AnswerEffort Quick = new("quick", 6, 2, 8);
    public static readonly AnswerEffort Balanced = new("balanced", 8, 2, 10);
    public static readonly AnswerEffort Thorough = new("thorough", 10, 3, 12);
    public static readonly AnswerEffort Deep = new("deep", 12, 3, 14);
    public static readonly AnswerEffort Max = new("max", 14, 4, 16);

    public static AnswerEffort Parse(string? value) => value switch
    {
        null or "quick" => Quick,
        "balanced" => Balanced,
        "thorough" => Thorough,
        "deep" => Deep,
        "max" => Max,
        _ => throw new ApiException(StatusCodes.Status400BadRequest, "Neznámá náročnost odpovědi."),
    };
}
