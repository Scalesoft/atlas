using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Providers;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Ai.Settings.Commands;
using Atlas.Server.Features.Ai.Settings.Domain;
using Atlas.Server.Features.Ai.Usage;
using Atlas.Server.Tests.Ai.Fakes;
using Atlas.Server.Tests.Platform;
using Xunit;

namespace Atlas.Server.Tests.Ai;

/// <summary>
/// A2: the real factory against local Kestrel stubs. No model is involved: the stubs answer in the OpenAI and Anthropic wire
/// formats, so the tests see what actually leaves Atlas (key, path) and that a redirect never takes the key elsewhere.
/// </summary>
public sealed class AiClientFactoryTests
{
    private const int MaxTimeoutSeconds = 300;
    private const string Key = "factory-fixture-credential";

    [Theory]
    [InlineData(AiProvider.OpenAiCompatible)]
    [InlineData(AiProvider.Gateway)]
    [InlineData(AiProvider.Anthropic)]
    public async Task Catalogue_reads_only_bounded_model_metadata_without_inference(string provider)
    {
        var pages = 0;
        await using var server = await StubServer.StartAsync(async context =>
        {
            Assert.Equal("/v1/models", context.Request.Path);
            pages++;
            if (provider == AiProvider.Anthropic && pages == 2)
            {
                Assert.Equal("alpha", context.Request.Query["after_id"]);
            }
            await context.Response.WriteAsJsonAsync(new
            {
                data = new[] { new { id = "zulu", display_name = "Zulu" }, new { id = "alpha", display_name = "Alpha" }, new { id = "alpha", display_name = "Alpha" } },
                has_more = provider == AiProvider.Anthropic && pages == 1, last_id = "alpha",
            });
        });
        using var ai = new FactoryHost();
        var profile = ai.Protect(FakeAi.Profile(AiProfileKind.Chat) with
        {
            Provider = provider, BaseUrl = server.Url + (provider == AiProvider.Anthropic ? "" : "/v1"), Model = "",
        }, Key);
        var result = await ai.Factory.ListModelsAsync(profile, default);
        Assert.True(result.Ok);
        Assert.Equal(["alpha", "zulu"], result.Models.Select(x => x.Id));
        Assert.Equal(provider == AiProvider.Anthropic ? 2 : 1, pages);
        Assert.All(server.Requests, call =>
        {
            Assert.Equal("GET", call.Method);
            Assert.Equal(provider == AiProvider.Anthropic ? Key : "Bearer " + Key,
                provider == AiProvider.Anthropic ? call.ApiKey : call.Authorization);
        });
        Assert.Empty(ai.Usage.Drain());
    }

    [Fact]
    public async Task Catalogue_stops_at_five_pages_and_five_hundred_models()
    {
        var page = 0;
        await using var server = await StubServer.StartAsync(async context =>
        {
            var start = page++ * 100;
            await context.Response.WriteAsJsonAsync(new
            {
                data = Enumerable.Range(start, 100).Select(i => new { id = $"model-{i:D3}" }),
                has_more = true, last_id = $"model-{start + 99:D3}",
            });
        });
        using var ai = new FactoryHost();
        var profile = ai.Protect(FakeAi.Profile(AiProfileKind.Chat) with { Provider = AiProvider.Anthropic, BaseUrl = server.Url }, Key);
        var result = await ai.Factory.ListModelsAsync(profile, default);
        Assert.True(result.Ok);
        Assert.Equal(500, result.Models.Count);
        Assert.Equal("model-499", result.Models[^1].Id);
        Assert.Equal(5, page);
    }

    [Fact]
    public async Task Catalogue_refuses_redirects_without_forwarding_credentials()
    {
        await using var other = await StubServer.StartAsync(_ => Task.CompletedTask);
        await using var server = await StubServer.StartAsync(context =>
        {
            context.Response.StatusCode = 307;
            context.Response.Headers.Location = other.Url + "/v1/models";
            return Task.CompletedTask;
        });
        using var ai = new FactoryHost();
        var result = await ai.Factory.ListModelsAsync(ai.Protect(FakeAi.Profile(AiProfileKind.Chat) with { BaseUrl = server.Url + "/v1", Model = "" }, Key), default);
        Assert.False(result.Ok);
        Assert.Equal(AiOutcome.RedirectRefused, result.Code);
        Assert.Empty(other.Requests);
        Assert.DoesNotContain(ai.Logs.Messages, line => line.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Gateway_uses_openai_json_chat_and_model_probe_with_the_saved_key()
    {
        string? body = null;
        await using var server = await StubServer.StartAsync(async context =>
        {
            if (context.Request.Path == "/v1/models")
            {
                await context.Response.WriteAsJsonAsync(new
                {
                    @object = "list", data = new[] { new { id = "gateway-test", @object = "model", created = 0, owned_by = "gateway" } },
                });
                return;
            }
            if (context.Request.Path == "/v1/chat/completions")
            {
                body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                await context.Response.WriteAsJsonAsync(new
                {
                    id = "chatcmpl-gateway-fixture", @object = "chat.completion", created = 0, model = "gateway-test",
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "{\"ok\":true}" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 4, completion_tokens = 3, total_tokens = 7 },
                });
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        using var ai = new FactoryHost();
        var profile = ai.Protect(FakeAi.Profile(AiProfileKind.Chat) with
        {
            Provider = "gateway", BaseUrl = server.Url + "/v1", Model = "gateway-test", NativeJsonSchema = true, ReasoningEffort = "none",
        }, Key);

        using var chat = ai.Factory.Chat(profile, AiTaskCatalog.AdminTestChat);
        Assert.True((await chat.GetResponseAsync<GatewayAnswer>("Potvrď.", JsonSerializerOptions.Web)).Result.Ok);
        using var sent = JsonDocument.Parse(Assert.IsType<string>(body));
        Assert.Equal("gateway-test", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", sent.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("none", sent.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(new AiProbeResult(true, null, true), await ai.Factory.ProbeAsync(profile, default));
        // Gateway serves its one loaded model whatever the profile names: still available, with the served model to warn about.
        // Another OpenAI-compatible server at the same URL keeps the strict missing-model verdict.
        Assert.Equal(new AiProbeResult(true, null, false, "gateway-test"), await ai.Factory.ProbeAsync(profile with { Model = "absent-model" }, default));
        Assert.Equal(new AiProbeResult(false, AiOutcome.ModelNotFound, false),
            await ai.Factory.ProbeAsync(profile with { Provider = AiProvider.OpenAiCompatible, Model = "absent-model" }, default));
        Assert.All(server.Requests, call => Assert.Equal("Bearer " + Key, call.Authorization));
        Assert.Equal([("POST", "/v1/chat/completions"), ("GET", "/v1/models"), ("GET", "/v1/models"), ("GET", "/v1/models")],
            server.Requests.Select(call => (call.Method, call.Path)));
        var usage = Assert.Single(ai.Usage.Drain());
        Assert.Equal((AiOutcome.Ok, "gateway-test", (long?)4, (long?)3), (usage.Outcome, usage.Model, usage.InputTokens, usage.OutputTokens));
        Assert.Throws<AiConfigurationException>(() => ai.Factory.Embeddings(profile with { Kind = AiProfileKind.Embeddings }, AiTaskCatalog.SearchEmbedQuery));
        Assert.Throws<AiConfigurationException>(() => ai.Factory.Chat(profile with { Provider = "unknown-provider" }, AiTaskCatalog.AdminTestChat));
        Assert.Throws<AiConfigurationException>(() => ai.Factory.Chat(profile with { Location = AiLocation.Cloud }, AiTaskCatalog.AdminTestChat));
        Assert.Throws<AiConfigurationException>(() => ai.Factory.Chat(profile with { BaseUrl = null }, AiTaskCatalog.AdminTestChat));
        Assert.Equal(new AiProbeResult(false, AiOutcome.BadRequest, false), await ai.Factory.ProbeAsync(profile with { Provider = "unknown-provider" }, default));
        Assert.Equal(new AiProbeResult(false, AiOutcome.BadRequest, false), await ai.Factory.ProbeAsync(profile with { BaseUrl = null }, default));
        Assert.Equal(4, server.Requests.Count);
    }

    private sealed record GatewayAnswer(bool Ok);

    [Fact]
    public async Task Openai_compatible_client_sends_the_key_to_the_profile_url_and_never_follows_redirects()
    {
        await using var other = await StubServer.StartAsync(_ => Task.CompletedTask);
        await using var server = await StubServer.StartAsync(async context =>
        {
            var path = context.Request.Path.Value!;
            if (path.StartsWith("/moved/", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = other.Url + path["/moved".Length..];
                return;
            }
            if (path == "/v1/embeddings")
            {
                var request = await JsonDocument.ParseAsync(context.Request.Body);
                var count = request.RootElement.GetProperty("input").GetArrayLength();
                await context.Response.WriteAsJsonAsync(new
                {
                    @object = "list",
                    data = Enumerable.Range(0, count).Select(i => new { @object = "embedding", index = i, embedding = Enumerable.Repeat(0.5f, FakeAi.Dimensions) }),
                    model = "text-embedding-bge-m3",
                    usage = new { prompt_tokens = 7, total_tokens = 7 },
                });
                return;
            }
            if (path == "/v1/models")
            {
                await context.Response.WriteAsJsonAsync(new { @object = "list", data = new[] { new { id = "text-embedding-bge-m3", @object = "model", created = 0, owned_by = "lmstudio" } } });
                return;
            }
            context.Response.StatusCode = StatusCodes.Status404NotFound;
        });
        using var ai = new FactoryHost();
        var profile = ai.Protect(FakeAi.Profile(AiProfileKind.Embeddings) with { BaseUrl = server.Url + "/v1" }, Key);

        using var generator = ai.Factory.Embeddings(profile, AiTaskCatalog.SearchEmbedQuery);
        var vectors = await generator.GenerateAsync(["připojení k VPN"]);
        Assert.Equal(1.0, Math.Sqrt(vectors[0].Vector.ToArray().Sum(x => (double)x * x)), 5);
        var call = Assert.Single(server.Requests);
        Assert.Equal(("POST", "/v1/embeddings", "Bearer " + Key), (call.Method, call.Path, call.Authorization));
        Assert.Equal(new AiProbeResult(true, null, true), await ai.Factory.ProbeAsync(profile, default));
        Assert.Equal(new AiProbeResult(false, AiOutcome.ModelNotFound, false), await ai.Factory.ProbeAsync(profile with { Model = "other-model" }, default));

        // A 307 to another origin is refused: the key never reaches it, and the outcome is a code, not the location.
        using var moved = ai.Factory.Embeddings(profile with { BaseUrl = server.Url + "/moved/v1" }, AiTaskCatalog.SearchEmbedQuery);
        var refused = await Assert.ThrowsAsync<AiCallException>(() => moved.GenerateAsync(["připojení k VPN"]));
        Assert.Equal(AiOutcome.RedirectRefused, refused.Code);
        Assert.Empty(other.Requests);
        Assert.Equal([AiOutcome.Ok, AiOutcome.RedirectRefused], ai.Usage.Drain().Select(x => x.Outcome));
        Assert.DoesNotContain(ai.Logs.Messages, line => line.Contains(Key, StringComparison.Ordinal) || line.Contains(other.Url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Anthropic_profile_refuses_embeddings()
    {
        await using var server = await StubServer.StartAsync(_ => Task.CompletedTask);
        using var ai = new FactoryHost();
        var profile = ai.Protect(FakeAi.Profile(AiProfileKind.Chat) with { Provider = AiProvider.Anthropic, Location = AiLocation.Cloud, BaseUrl = server.Url }, Key);
        Assert.Throws<AiConfigurationException>(() => ai.Factory.Embeddings(profile, AiTaskCatalog.SearchEmbedQuery));
        Assert.Throws<AiConfigurationException>(() => ai.Factory.Embeddings(profile with { Kind = AiProfileKind.Embeddings }, AiTaskCatalog.SearchEmbedQuery));
        Assert.Empty(server.Requests);
        Assert.Empty(ai.Usage.Drain());
    }

    /// <summary>The open question of the plan (§6): the official Anthropic SDK takes Atlas's HttpClient, so its calls refuse
    /// redirects too, and it never falls back to ANTHROPIC_* environment variables.</summary>
    [Fact]
    public async Task Anthropic_client_uses_the_atlas_http_client_and_never_follows_redirects()
    {
        await using var other = await StubServer.StartAsync(_ => Task.CompletedTask);
        await using var server = await StubServer.StartAsync(context =>
        {
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = other.Url + context.Request.Path;
            return Task.CompletedTask;
        });
        using var ai = new FactoryHost();
        var profile = ai.Protect(FakeAi.Profile(AiProfileKind.Chat) with
        {
            Provider = AiProvider.Anthropic, Location = AiLocation.Cloud, BaseUrl = server.Url, Model = "claude-test",
        }, Key);
        using var chat = ai.Factory.Chat(profile, AiTaskCatalog.AdminTestChat);
        var refused = await Assert.ThrowsAsync<AiCallException>(() => chat.GetResponseAsync("Ahoj"));
        Assert.Equal(AiOutcome.RedirectRefused, refused.Code);
        var call = Assert.Single(server.Requests);
        Assert.Equal(("POST", "/v1/messages"), (call.Method, call.Path));
        // SDK 13 sends the API key of a message as a bearer token (the models list still as x-api-key).
        Assert.True(call.ApiKey == Key || call.Authorization == "Bearer " + Key);
        Assert.Empty(other.Requests);
    }

    /// <summary>The real AddAi registrations (named HttpClient, data protection, pipeline) without a database.</summary>
    private sealed class FactoryHost : IDisposable
    {
        private readonly ServiceProvider m_services;

        public FactoryHost()
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
            // These tests are about the key and the redirects, not timeouts. A frozen clock means the profile timeout (and the
            // probe's) can never fire however long a starved test host takes to schedule the stub and the client; the SDK's own
            // real-time network timeout is the profile maximum plus its margin.
            services.AddSingleton<TimeProvider>(new FakeTimeProvider());
            services.AddAi();
            services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            m_services = services.BuildServiceProvider();
        }

        public CaptureLoggerProvider Logs { get; } = new();
        public IAiClientFactory Factory => m_services.GetRequiredService<IAiClientFactory>();
        public AiUsageRecorder Usage => m_services.GetRequiredService<AiUsageRecorder>();

        public AiProfileSnapshot Protect(AiProfileSnapshot profile, string key) => profile with
        {
            TimeoutSeconds = MaxTimeoutSeconds,
            ProtectedApiKey = m_services.GetRequiredService<IDataProtectionProvider>().CreateProtector(SaveAiProfileHandler.ApiKeyPurpose).Protect(key),
        };

        public void Dispose() => m_services.Dispose();
    }

    internal sealed record StubRequest(string Method, string Path, string? Authorization, string? ApiKey);

    /// <summary>A Kestrel server on a free loopback port that records every request it receives.</summary>
    internal sealed class StubServer : IAsyncDisposable
    {
        private readonly WebApplication m_app;
        private readonly ConcurrentQueue<StubRequest> m_requests = new();

        private StubServer(WebApplication app) => m_app = app;

        public string Url => m_app.Urls.First();
        public IReadOnlyList<StubRequest> Requests => m_requests.ToArray();

        public static async Task<StubServer> StartAsync(Func<HttpContext, Task> handler)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var stub = new StubServer(app);
            app.Run(context =>
            {
                stub.m_requests.Enqueue(new StubRequest(context.Request.Method, context.Request.Path.Value ?? "",
                    context.Request.Headers.Authorization.FirstOrDefault(), context.Request.Headers["x-api-key"].FirstOrDefault()));
                return handler(context);
            });
            await app.StartAsync();
            return stub;
        }

        public async ValueTask DisposeAsync() => await m_app.DisposeAsync();
    }
}
