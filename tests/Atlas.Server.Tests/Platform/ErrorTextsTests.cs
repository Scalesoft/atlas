using System.Text.Json;
using Atlas.Server.Platform.Localization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.Server.Tests.Platform;

/// <summary>Atlas 4.1: a problem title in the request's language (ErrorTexts, RequestLanguage); the Czech text is the key and the fallback.</summary>
public sealed class ErrorTextsTests
{
    [Theory]
    [InlineData(null, null, "Stránka nebyla nalezena.")]
    [InlineData(null, "en-US,en;q=0.9", "Page not found.")]
    [InlineData(null, "cs-CZ,en;q=0.5", "Stránka nebyla nalezena.")]
    [InlineData("en", "cs-CZ", "Page not found.")]
    [InlineData("cs", "de", "Stránka nebyla nalezena.")]
    public async Task A_problem_answers_in_the_account_choice_else_the_browser_else_Czech(string? cookie, string? acceptLanguage, string title)
    {
        var problem = await ProblemOf(new ApiException(404, "Stránka nebyla nalezena."), cookie, acceptLanguage);
        Assert.Equal(title, problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_text_without_English_stays_Czech_and_values_fill_either_language()
    {
        var untranslated = await ProblemOf(new ApiException(409, "Tento text nemá anglický překlad.", "some_code"), "en", null);
        Assert.Equal("Tento text nemá anglický překlad.", untranslated.GetProperty("title").GetString());
        Assert.Equal("some_code", untranslated.GetProperty("code").GetString());

        var values = new Dictionary<string, object?> { ["field"] = "title" };
        var czech = new ApiException(400, "Pole {field} v těle požadavku není platný JSON nebo má jiný typ hodnoty (zpětné lomítko v cestě se píše jako \\\\).", args: values);
        Assert.StartsWith("Pole title v těle", czech.Message);
        Assert.StartsWith("The field title of the request body", (await ProblemOf(czech, "en", null)).GetProperty("title").GetString());
    }

    [Fact]
    public void Every_English_text_translates_a_Czech_text_the_server_still_has()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "Atlas.Server");
        var source = string.Concat(Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));
        // A key as a C# literal escapes a backslash and a quote.
        var orphans = ErrorTexts.EnglishTexts.Keys.Where(czech => !source.Contains(czech.Replace("\\", "\\\\").Replace("\"", "\\\""), StringComparison.Ordinal)).ToArray();
        Assert.True(orphans.Length == 0, "English texts whose Czech source is gone (edit the key with the text):\n" + string.Join("\n", orphans));
    }

    private static async Task<JsonElement> ProblemOf(Exception error, string? cookie, string? acceptLanguage)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{RequestLanguage.Cookie}={cookie}";
        }
        if (acceptLanguage is not null)
        {
            context.Request.Headers.AcceptLanguage = acceptLanguage;
        }
        await RequestErrorHandling.InvokeAsync(context, () => throw error, NullLogger.Instance);
        return JsonDocument.Parse(((MemoryStream)context.Response.Body).ToArray()).RootElement;
    }

    private static string RepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string path = "")
        => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))))!;
}
