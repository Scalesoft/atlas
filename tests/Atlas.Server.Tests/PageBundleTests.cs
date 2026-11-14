using Atlas.Server.Features.Pages.Contracts;
using Xunit;

namespace Atlas.Server.Tests;

public sealed class PageBundleTests
{
    [Fact]
    public void Bundle_asks_for_the_contacts_and_pages_a_document_names()
    {
        var contact = Guid.NewGuid();
        var chip = Guid.NewGuid();
        var linked = Guid.NewGuid();
        var json = """
            {"document":{"type":"doc","content":[{"type":"paragraph","content":[
              {"type":"contactInline","attrs":{"data":{"contacts":[{"name":"A","objectId":"CONTACT"},{"name":"B"}]}}},
              {"type":"pageReference","attrs":{"id":"CHIP"}},
              {"type":"text","text":"x","marks":[{"type":"link","href":"#/pages/LINKED"}]}]}]}}
            """.Replace("CONTACT", contact.ToString()).Replace("CHIP", chip.ToString()).Replace("LINKED", linked.ToString());
        var (contacts, pages) = PageBundle.ReferencedIds(json);
        Assert.Equal([contact], contacts);
        Assert.Equal(new[] { chip, linked }.Order(), pages.Order());
        Assert.Empty(PageBundle.ReferencedIds("not json").Contacts);
    }
}
