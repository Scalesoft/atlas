using System.Text.Json;
using Atlas.Markdown.Sync.Recognition;

namespace Atlas.Markdown.Sync.Tests;

/// <summary>
/// The golden corpus of the linking engine (tests/fixtures/linking/corpus.json) through the C# core: the same typed text gives the same
/// decisions as the editor's pure core (core/recognition/corpus.test.ts „the pure core“), and the document cases the same hits.
/// </summary>
public sealed class RecognitionCorpusTests
{
    private static readonly JsonElement Corpus = Load();

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "fixtures", "linking", "corpus.json")))
        {
            directory = directory.Parent;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", "linking", "corpus.json")));
        return document.RootElement.Clone();
    }

    public static RecognitionLexicon Lexicon(string name)
    {
        var lexicon = Corpus.GetProperty("lexicons").GetProperty(name);
        return new RecognitionLexicon(lexicon.GetProperty("truncated").GetBoolean(),
            lexicon.GetProperty("people").EnumerateArray().Select(person => new RecognitionPerson(person.GetProperty("key").GetString()!,
                Guid.TryParse(person.GetProperty("contactId").GetString(), out var contact) ? contact : null, null, person.GetProperty("name").GetString()!,
                person.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString()!).ToArray())).ToArray(),
            lexicon.GetProperty("pages").EnumerateArray().Select(page => new RecognitionPage(page.GetProperty("id").GetGuid(), page.GetProperty("title").GetString()!)).ToArray(),
            lexicon.GetProperty("youTrackProjects").EnumerateArray().Select(project => project.GetString()!).ToArray());
    }

    private static string Decision(RecognitionDecision decision) => decision.ToString().ToLowerInvariant();

    [Fact]
    public void Typing_cases_give_the_editor_cores_hits()
    {
        var failures = new List<string>();
        var cases = Corpus.GetProperty("typing").EnumerateArray().ToArray();
        foreach (var test in cases)
        {
            var expected = test.GetProperty("hits").EnumerateArray().Select(hit => $"{hit.GetProperty("text").GetString()}:{hit.GetProperty("decision").GetString()}").ToArray();
            var actual = new List<string>();
            if (!test.TryGetProperty("block", out var block) || block.GetString() != "heading")
            {
                var lexicon = Lexicon(test.GetProperty("lexicon").GetString()!);
                var index = lexicon.Index();
                // Text inside a link is not prose: the editor reads it as U+FFFC.
                var text = new string('￼', test.TryGetProperty("linked", out var linked) ? linked.GetString()!.Length : 0);
                foreach (var c in test.GetProperty("typed").GetString()!)
                {
                    text += c;
                    if (index.RecognizeTail(RecognitionIndex.RecognitionWindow(text, text.Length).Text, lexicon.YouTrackProjects) is { Decision: not RecognitionDecision.None } hit)
                    {
                        actual.Add($"{hit.Text}:{Decision(hit.Decision)}");
                    }
                }
            }
            if (!expected.SequenceEqual(actual))
            {
                failures.Add($"{test.GetProperty("name").GetString()}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
            }
        }
        Assert.True(cases.Length >= 16, "corpus.json holds the typing cases");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Document_cases_give_their_hits()
    {
        var failures = new List<string>();
        var cases = Corpus.GetProperty("documents").EnumerateArray().ToArray();
        foreach (var test in cases)
        {
            var lexicon = Lexicon(test.GetProperty("lexicon").GetString()!);
            var expected = test.GetProperty("hits").EnumerateArray().Select(hit => $"{hit.GetProperty("kind").GetString()}:{hit.GetProperty("text").GetString()}:{hit.GetProperty("decision").GetString()}").ToArray();
            var actual = lexicon.Index().RecognizeBlock(test.GetProperty("text").GetString()!, lexicon.YouTrackProjects)
                .Select(hit => $"{hit.Kind}:{hit.Text}:{Decision(hit.Decision)}").ToArray();
            if (!expected.SequenceEqual(actual))
            {
                failures.Add($"{test.GetProperty("name").GetString()}: expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}]");
            }
        }
        Assert.NotEmpty(cases);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Fold_is_invariant_and_drops_marks()
    {
        Assert.Equal("jana novakova", RecognitionText.Fold("JANA Nováková"));
        Assert.Equal("jana novakova", RecognitionText.NameKey("Jana  Nováková"));
        Assert.Equal(["px2-1235", "o’brien"], RecognitionText.Tokenize("PX2-1235 O’Brien").Select(token => token.Key));
        Assert.Equal("novakov", RecognitionText.StemOf("novakove"));
        Assert.Equal("petr", RecognitionText.StemOf("petrovi"));
    }
}
