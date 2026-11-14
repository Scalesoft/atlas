using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Assistant.Domain;
using Xunit;

namespace Atlas.Server.Tests.Assistant;

/// <summary>R5 (O4): a conversation continues only on a compatible chat revision; nothing moves it to another provider silently.</summary>
public sealed class ChatProfileCompatibilityTests
{
    private static AiProfileSnapshot Chat(string provider = "openai-compatible", string location = "company",
        string? baseUrl = "http://lmstudio.example:1234/v1", string model = "gemma-4", int timeout = 120) => new(Guid.NewGuid(), "chat", true,
        provider, location, baseUrl, model, null, timeout, 2, "none", null, true, null, "", "", false, false, null, null, null);

    [Fact]
    public void Key_rotation_timeout_and_path_keep_a_conversation_but_provider_location_origin_or_model_do_not()
    {
        var pinned = ChatProfileCompatibility.Fingerprint(Chat());

        Assert.True(ChatProfileCompatibility.IsCompatible(pinned, Chat(timeout: 30) with { ProtectedApiKey = "rotated" }));
        Assert.True(ChatProfileCompatibility.IsCompatible(pinned, Chat(baseUrl: "HTTP://LMSTUDIO.example:1234/api/v1")));
        Assert.False(ChatProfileCompatibility.IsCompatible(pinned, Chat(model: "gemma-4-31b")));
        Assert.False(ChatProfileCompatibility.IsCompatible(pinned, Chat(location: "cloud")));
        Assert.False(ChatProfileCompatibility.IsCompatible(pinned, Chat(baseUrl: "http://lmstudio.example:8080/v1")));
        Assert.False(ChatProfileCompatibility.IsCompatible(pinned, Chat(provider: "anthropic", baseUrl: null)));
        // Anthropic without a URL is its official API: the same origin as when it is spelled out.
        Assert.True(ChatProfileCompatibility.IsCompatible(ChatProfileCompatibility.Fingerprint(Chat("anthropic", "cloud", null, "claude")),
            Chat("anthropic", "cloud", "https://api.anthropic.com/v1", "claude")));
    }
}
