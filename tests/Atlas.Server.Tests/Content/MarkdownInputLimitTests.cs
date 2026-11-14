using Atlas.Server.Features.PageLinks;
using Atlas.Server.Platform.Content;
using Xunit;

namespace Atlas.Server.Tests.Content;

/// <summary>
/// Markdown a client sends is parsed before anything else looks at it (the page links of every Markdown write): input the parser
/// cannot take is the client's 400 invalid_markdown, never a 500.
/// </summary>
public sealed class MarkdownInputLimitTests
{
    [Theory]
    [InlineData("quote")]
    [InlineData("list")]
    [InlineData("link")]
    public void Too_deeply_nested_markdown_is_invalid_markdown(string shape)
    {
        var markdown = shape switch
        {
            "quote" => new string('>', 5000) + " text",
            "list" => string.Concat(Enumerable.Range(0, 300).Select(level => new string(' ', level * 2) + "- x\n")),
            _ => new string('[', 50000) + "x" + new string(']', 50000)
        };

        var error = Assert.Throws<ApiException>(() => MarkdownLinks.Collect(markdown));
        Assert.Equal((400, "invalid_markdown"), (error.Status, error.Code));
    }
}
