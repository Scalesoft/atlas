using Atlas.Server.Features.Assistant.Answering;
using Microsoft.Extensions.AI;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>K8 (review cycle 1): a model that ignores the JSON schema is read tolerantly, retried once, and never gets invented citations.</summary>
public sealed class AnswerOutputTests
{
    private static readonly string[] Titles = ["KBN — Production", "KBN — Production", "Keepass", "KBN — Production"];

    [Fact]
    public void Plain_string_sentences_are_read_but_get_no_source_they_do_not_name()
    {
        var result = AnswerOutput.Parse(RecordedAnswers.StringSentences, Titles);

        Assert.NotNull(result);
        Assert.True(result.Answerable);
        Assert.Equal(4, result.Sentences.Count);
        Assert.StartsWith("U strukturovaných požadavků v KBN", result.Sentences[0].Text);
        Assert.All(result.Sentences, sentence => Assert.Empty(sentence.Sources));
        Assert.True(AnswerValidator.Validate(result, Titles.Length).NoSources);
    }

    [Fact]
    public void A_plain_string_keeps_only_the_citations_it_writes_or_the_source_titles_it_names()
    {
        var result = AnswerOutput.Parse("""{"answerable":true,"sentences":["Heslo je v trezoru Keepass.","Certifikát je hotový [2], viz (9)."]}""", Titles);

        Assert.Equal([3], result!.Sentences[0].Sources);
        Assert.Equal([2, 9], result.Sentences[1].Sources);
        var validated = AnswerValidator.Validate(result, Titles.Length);
        Assert.Equal(["Heslo je v trezoru Keepass.", "Certifikát je hotový, viz."], validated.Sentences.Select(sentence => sentence.Text));
        Assert.Equal(1, validated.InvalidCitations);
    }

    [Theory]
    [InlineData("To není JSON.")]
    [InlineData("""{"answerable":"ano","sentences":[]}""")]
    [InlineData("""{"answerable":true,"sentences":"Věta."}""")]
    [InlineData("""["Věta."]""")]
    public void Output_that_is_no_answer_is_null(string text) => Assert.Null(AnswerOutput.Parse(text, Titles));

    [Fact]
    public void Missing_properties_are_an_abstention_as_the_deserializer_read_them()
        => Assert.Equal((false, 0), AnswerOutput.Parse("""{"ok":true}""", Titles) is { } result ? (result.Answerable, result.Sentences.Count) : (true, -1));

    [Fact]
    public async Task An_unusable_answer_is_asked_again_once_with_a_correction()
    {
        var chat = new ScriptedChat(RecordedAnswers.StringSentences, RecordedAnswers.ObjectSentences);

        var completion = await AnswerOutput.GetAsync(chat, [new ChatMessage(ChatRole.User, "Otázka")], Titles, new ChatOptions(), CancellationToken.None);

        Assert.Equal(2, chat.Calls.Count);
        var retry = chat.Calls[1];
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], retry.Select(message => message.Role));
        Assert.Equal(RecordedAnswers.StringSentences, retry[1].Text);
        Assert.Equal(AnswerOutput.Correction, retry[2].Text);
        Assert.Equal([4], AnswerValidator.Validate(completion.Result, Titles.Length).Cited);
        Assert.Equal(2 * ScriptedChat.TokensPerCall, completion.Response.Usage!.OutputTokenCount);
    }

    [Fact]
    public async Task A_usable_answer_or_an_abstention_is_not_asked_again_and_a_second_failure_is_the_last()
    {
        var good = new ScriptedChat(RecordedAnswers.ObjectSentences);
        Assert.NotNull((await AnswerOutput.GetAsync(good, [], Titles, new ChatOptions(), CancellationToken.None)).Result);
        Assert.Single(good.Calls);

        var abstained = new ScriptedChat("""{"answerable":false,"sentences":[]}""");
        Assert.False((await AnswerOutput.GetAsync(abstained, [], Titles, new ChatOptions(), CancellationToken.None)).Result!.Answerable);
        Assert.Single(abstained.Calls);

        var broken = new ScriptedChat("nic", "zase nic", "nevolat");
        Assert.Null((await AnswerOutput.GetAsync(broken, [], Titles, new ChatOptions(), CancellationToken.None)).Result);
        Assert.Equal(2, broken.Calls.Count);

        // A tolerant first read survives a broken retry: the answer then ends "no sources", not as a failure.
        var strings = new ScriptedChat(RecordedAnswers.StringSentences, "nic");
        Assert.True(AnswerValidator.Validate((await AnswerOutput.GetAsync(strings, [], Titles, new ChatOptions(), CancellationToken.None)).Result, Titles.Length).NoSources);
    }

    /// <summary>P2-14: an output the token limit cut off is not asked for again (the retry would be cut the same way), and the
    /// completion says whether it was asked again.</summary>
    [Fact]
    public async Task An_output_cut_by_the_token_limit_is_not_asked_again()
    {
        var cut = new ScriptedChat("""{"answerable":true,"sentences":[{"text":"Zač""") { FinishReason = ChatFinishReason.Length };
        var completion = await AnswerOutput.GetAsync(cut, [], Titles, new ChatOptions(), CancellationToken.None);
        var retried = await AnswerOutput.GetAsync(new ScriptedChat("nic", RecordedAnswers.ObjectSentences), [], Titles, new ChatOptions(), CancellationToken.None);

        Assert.Single(cut.Calls);
        Assert.Null(completion.Result);
        Assert.False(completion.Retried);
        Assert.True(retried.Retried);
    }

    private sealed class ScriptedChat(params string[] answers) : IChatClient
    {
        public const int TokensPerCall = 7;

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public ChatFinishReason? FinishReason { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answers[Calls.Count - 1]))
            {
                Usage = new UsageDetails { InputTokenCount = 1, OutputTokenCount = TokensPerCall }, FinishReason = FinishReason,
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
