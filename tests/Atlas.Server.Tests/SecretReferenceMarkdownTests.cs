using Atlas.Server.Features.ExternalSharing.Contracts;
using Atlas.Server.Features.Search.Domain;
using Xunit;

namespace Atlas.Server.Tests;

/// <summary>The Markdown form of password references (editor 0.6.0 directives): the same contract as the rich JSON on every save,
/// filtered from every reader-facing copy, never in search snippets or public sharing.</summary>
public sealed class SecretReferenceMarkdownTests
{
    private const string Id = SecretReferenceContentTests.SecretId;
    private const string Other = "8d2f7a1c-5b3e-4c6d-9e0f-1a2b3c4d5e6f";
    private static readonly Guid SecretGuid = Guid.Parse(Id);
    private static readonly Guid OtherGuid = Guid.Parse(Other);

    [Theory]
    [InlineData("::secret{secretId=" + Id + "}")]
    [InlineData("VPN :secret{secretId=" + Id + " fields=\"username password\" autoHideSeconds=null} tady")]
    [InlineData("> - ::secret{secretId=" + Id + " field=url fields=url showCopy=false showDetails=true autoHideSeconds=90}")]
    public void Directives_of_the_plugin_are_collected(string markdown)
    {
        Assert.Equal([SecretGuid], SecretReferenceMarkdown.Collect(markdown));
    }

    [Theory]
    [InlineData(":secret{secretId=" + Id + " value=CANARY}")]
    [InlineData(":secret{secretId=" + Id + " password=\"CANARY\"}")]
    [InlineData("::secret{secretId=" + Id + " label=CANARY}")]
    [InlineData(":secret{secretId=" + Id + " secretId=" + Other + "}")]
    [InlineData(":secret{field=password}")]
    [InlineData(":secret{secretId=2538F90A-733E-4F69-BAFE-5F71086739BE}")]
    [InlineData(":secret{secretId=" + Id + " autoHideSeconds=4}")]
    [InlineData(":secret{secretId=" + Id + " showCopy=yes}")]
    [InlineData(":secret{secretId=" + Id + " fields=\"password password\"}")]
    [InlineData(":secret{secretId=" + Id + " CANARY}")]
    [InlineData(":secret{CANARY}")]
    public void Directives_outside_the_contract_are_refused_without_echoing_input(string markdown)
    {
        var error = Assert.Throws<InvalidDataException>(() => SecretReferenceMarkdown.Collect(markdown));
        Assert.DoesNotContain("CANARY", error.Message);
        Assert.DoesNotContain(Id, error.Message);
        // A reader copy loses such a directive entirely (fail closed).
        Assert.DoesNotContain("secret{", SecretReferenceMarkdown.Strip(markdown, _ => true, out _));
    }

    [Theory]
    [InlineData("```md\n::secret{value=CANARY}\n```")]
    [InlineData("> ~~~\n> :secret{value=CANARY}\n> ~~~")]
    [InlineData("Syntax `:secret{value=CANARY}` is text.")]
    [InlineData("An open :secret{secretId=" + Id + " that never closes")]
    [InlineData("Nested :secret{secretId={x}}")]
    public void Code_and_unclosed_braces_are_text(string markdown)
    {
        Assert.Empty(SecretReferenceMarkdown.Collect(markdown));
        Assert.Equal(markdown, SecretReferenceMarkdown.Strip(markdown, _ => false, out var removed));
        Assert.Equal(0, removed);
    }

    [Fact]
    public void Saved_markdown_may_name_only_references_its_json_holds()
    {
        var markdown = "::secret{secretId=" + Id + "}\n\nText :secret{secretId=" + Other + "}";
        SecretReferenceMarkdown.RequireWithin(markdown, new HashSet<Guid> { SecretGuid, OtherGuid });
        Assert.Throws<InvalidDataException>(() => SecretReferenceMarkdown.RequireWithin(markdown, new HashSet<Guid> { SecretGuid }));
        var error = Assert.Throws<ApiException>(() => Rules.ValidateDocument(SecretReferenceGuardTestsRich(), markdown));
        Assert.Equal((400, "invalid_secret_reference"), (error.Status, error.Code));
    }

    [Fact]
    public void Readers_lose_exactly_the_directives_they_may_not_discover()
    {
        var markdown = "# Síť\n\n::secret{secretId=" + Id + " showDetails=true}\n\nVPN :secret{secretId=" + Other + "} konec.\n";
        var copy = SecretReferenceMarkdown.Strip(markdown, id => id == SecretGuid, out var removed);
        Assert.Equal(1, removed);
        Assert.Equal("# Síť\n\n::secret{secretId=" + Id + " showDetails=true}\n\nVPN  konec.\n", copy);
        Assert.Same(markdown, SecretReferenceMarkdown.Strip(markdown, _ => true, out _));
        Assert.DoesNotContain("secret{", SecretReferenceMarkdown.Strip(markdown, _ => false, out _));
    }

    [Theory]
    [InlineData("Router ::secret{secretId=" + Id + " fields=\"username password\"} přístupy do sítě")]
    // ts_headline cuts a directive at either edge of the fragment.
    [InlineData("4e97-aab8-a74a4cf48c89 fields=\"username password\"} přístupy do sítě")]
    [InlineData("a74a4cf48c89} přístupy do sítě")]
    [InlineData("přístupy do sítě :secret{secretId=" + Id + " fie")]
    public void Search_snippets_never_carry_a_reference(string fragment)
    {
        var snippet = SearchText.Snippet(fragment, "pristupy");
        Assert.DoesNotContain("secret", snippet);
        Assert.DoesNotContain("a74a4cf48c89", snippet);
        Assert.DoesNotContain("fields", snippet);
        Assert.Contains("přístupy do sítě", snippet);
    }

    [Fact]
    public void Public_sharing_carries_no_markdown_projection()
    {
        // The shared copy is the rich JSON only, from which SharedDocument removes every reference.
        Assert.DoesNotContain(typeof(SharedPageDto).GetProperties(), property => property.Name.Contains("Markdown", StringComparison.Ordinal));
    }

    private static string SecretReferenceGuardTestsRich() =>
        SecretReferenceContentTests.Envelope("{\"type\":\"doc\",\"content\":[" + SecretReferenceContentTests.Reference + "]}", "[{\"id\":\"secrets\",\"version\":4}]");
}
