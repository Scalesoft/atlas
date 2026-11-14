using Atlas.Server.Features.Search.Domain;
using Atlas.Server.Features.SearchGaps;
using Xunit;

namespace Atlas.Server.Tests.SearchGaps;

/// <summary>G2/D9: a missed query is kept only when it is long enough to mean something and nothing in it looks like a secret
/// someone pasted into the search box by mistake.</summary>
public sealed class SearchMissPolicyTests
{
    [Fact]
    public void Secret_like_overlong_and_short_queries_are_never_recorded()
    {
        static bool Allows(string query) => SearchMissPolicy.Allows(query, SearchText.Normalize(query));

        Assert.True(Allows("Přístup do VPN z mobilu"));
        Assert.True(Allows("kód 2026 objednávky"));
        // Short: fewer than three normalized characters.
        Assert.False(Allows("vp"));
        Assert.False(Allows(" a! "));
        // Overlong: more than 120 characters, though every word is ordinary.
        Assert.False(Allows(string.Join(' ', Enumerable.Repeat("dovolená", 14))));
        // Secret-like: a word of 20+ characters, or 12+ characters mixing letters and digits, even inside punctuation.
        Assert.False(Allows("heslo ghp_aB3dE5fG7hJ9kL1mN"));
        Assert.False(Allows("token Abc123Def456"));
        Assert.False(Allows("klíč q0w9e8r7t6y5"));
        Assert.False(Allows("abcdefghijklmnopqrstuvwxyz"));
        Assert.True(Allows("objednávka 123456789012"));

        Assert.Equal("přístup do vpn", SearchMissPolicy.Display("  Přístup   do\tVPN "));
    }
}
