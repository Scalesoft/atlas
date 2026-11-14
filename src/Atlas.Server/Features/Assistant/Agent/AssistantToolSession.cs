using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Ai;
using Atlas.Server.Features.Ai.Capabilities;
using Atlas.Server.Features.Ai.Settings;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;
using Atlas.Server.Features.Projector.Persistence;

namespace Atlas.Server.Features.Assistant.Agent;

/// <summary>
/// The MCP tools of one assistant turn, in process. An MCP server built from the very tools /mcp serves and a client connected to it
/// over an in-memory transport, so a tool call runs the same tool code, G5 and content rights as an MCP client's. The identity is the
/// signed-in browser session of the current request, never a personal token: every message the server reads carries that request's
/// user and execution context, so <see cref="Platform.Identity.HttpCurrentActor"/> inside a tool sees the same actor as the endpoint,
/// and <see cref="Platform.Identity.HttpRequestChannel"/> the channel assistant.
/// <para>
/// Offered (spec §4.2, §5.1, D19): the router's selection (<see cref="AssistantToolGroups"/>) ∩ the tools that exist ∩ the tools
/// allowed for the assistant (a known scope, not <see cref="NeverOffered"/>). The API level is not read: read tools run, write tools
/// in a selected group are offered and every content right is enforced by the shared handlers. The session adds no group itself, so
/// <c>atlas_publish</c> is offered only when the router picked <c>publikace</c> from the last question (D18).
/// </para>
/// </summary>
public sealed class AssistantToolSession : IAsyncDisposable
{
    /// <summary>Spec §12: the most a single tool result gives the model.</summary>
    public const int OutputCap = 12_000;

    public const string TruncationNote =
        "\n…\n[Zkráceno: výstup nástroje pro asistenta má nejvýše 12 000 znaků a strukturovaná data se vynechala. Užší výsledek dá headingId, " +
        "spaceId nebo menší limit.]";

    public const string ConfirmationRequiredCode = "confirmation_required";

    /// <summary>Spec §5.1: never offered, whatever the router selects. Attachments are not text, embed layout and the trash stay human.</summary>
    public static readonly IReadOnlySet<string> NeverOffered = new HashSet<string>(StringComparer.Ordinal)
    {
        "atlas_download_attachment", "atlas_upload_attachment", "atlas_update_embed", "atlas_trash_page", "atlas_restore_page", "atlas_list_trash",
    };

    private readonly McpClient m_client;
    private readonly McpServer m_server;
    private readonly Task m_running;
    private readonly CancellationTokenSource m_stop;
    private readonly ProposalSlot m_proposal;

    private AssistantToolSession(McpClient client, McpServer server, Task running, CancellationTokenSource stop, IReadOnlyList<AITool> tools,
        AssistantToolResults results, ProposalSlot proposal)
    {
        m_client = client;
        m_server = server;
        m_running = running;
        m_stop = stop;
        Tools = tools;
        Results = results;
        m_proposal = proposal;
    }

    public IReadOnlyList<AITool> Tools { get; }

    /// <summary>What the tools returned so far in this turn (source candidates and display names).</summary>
    public AssistantToolResults Results { get; }

    /// <summary>The write proposal this turn made (at most one, spec §6.2), stored with the released answer.</summary>
    public AiProposalDraft? Proposal => m_proposal.Draft;

    /// <summary>A call of an offered tool through this session, as if the model made it: the same filter, sources and output cap.</summary>
    public Task<CallToolResult> CallAsync(string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
        => m_client.CallToolAsync(tool, arguments, cancellationToken: ct).AsTask();

    /// <param name="selection">The router's choice for this turn (<see cref="AssistantToolGroups.Select"/>).</param>
    public static Task<AssistantToolSession> OpenAsync(HttpContext context, AssistantToolSelection selection, CancellationToken ct)
        => OpenAsync(context, selection, approved: false, ct);

    /// <summary>
    /// Runs the call of a proposal the user confirmed (spec §6.1 step 4) in a fresh session offering only that tool, as the signed-in
    /// user on the channel assistant: the same MCP tool, shared handlers, content rights and G5 as any call, audited like a write over
    /// /mcp. Sets <see cref="HttpRequestChannel.ApprovedProposalMarker"/> on the request first. Tool errors come back as the result.
    /// </summary>
    public static async Task<CallToolResult> ExecuteAsync(HttpContext context, string tool, JsonObject arguments, CancellationToken ct)
    {
        context.Items[HttpRequestChannel.ApprovedProposalMarker] = true;
        await using var session = await OpenAsync(context, new AssistantToolSelection([], [tool]), approved: true, ct);
        using var document = JsonDocument.Parse(arguments.ToJsonString());
        var values = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value.Clone());
        return await session.m_client.CallToolAsync(tool, values, cancellationToken: ct);
    }

    private static async Task<AssistantToolSession> OpenAsync(HttpContext context, AssistantToolSelection selection, bool approved, CancellationToken ct)
    {
        var services = context.RequestServices;
        var logs = services.GetRequiredService<ILoggerFactory>();
        var user = context.User;
        // Spec §4.3: the tools resolve this very HttpContext (below), so their changes, openings and audit carry the channel
        // assistant. The whole request is the assistant's turn; a personal API identity with the marker is refused (401).
        context.Items[HttpRequestChannel.AssistantMarker] = true;
        // Captured inside the request, so IHttpContextAccessor in a tool resolves this request's HttpContext.
        var execution = ExecutionContext.Capture();
        var (clientSide, serverSide) = InProcessMcpTransport.CreatePair(message =>
            message.Context = new JsonRpcMessageContext { User = user, ExecutionContext = execution }, logs);

        var selected = selection.Tools.ToHashSet(StringComparer.Ordinal);
        var offered = services.GetServices<McpServerTool>()
            .Where(tool => selected.Contains(tool.ProtocolTool.Name) && !NeverOffered.Contains(tool.ProtocolTool.Name) && ScopeOf(tool) is not null)
            .ToArray();
        var scopes = offered.ToDictionary(tool => tool.ProtocolTool.Name, tool => ScopeOf(tool)!, StringComparer.Ordinal);
        var descriptions = offered.ToDictionary(tool => tool.ProtocolTool.Name,
            tool => tool.Metadata.OfType<AssistantDescriptionAttribute>().FirstOrDefault()?.Text, StringComparer.Ordinal);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "atlas-assistant", Version = AtlasVersion.Current },
            ServerInstructions = McpModule.Instructions,
            ToolCollection = [.. offered],
        };
        var results = new AssistantToolResults();
        var proposal = new ProposalSlot();
        options.Filters.Request.CallToolFilters.Add(next => Call(next, scopes, results, approved ? null : proposal));

        var server = McpServer.Create(serverSide, options, logs, services);
        var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        try
        {
            var client = await McpClient.CreateAsync(new InProcessClientTransport(clientSide),
                new McpClientOptions { ClientInfo = new Implementation { Name = "atlas-assistant", Version = AtlasVersion.Current } }, logs, ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            // The short Czech description where the tool has one; the /mcp description otherwise.
            // The one tool that needs a model (WP-50) is no MCP tool: it reads through this client, so rights, G5 and sources are the same.
            AITool[] extra = !approved && selected.Contains(SummarizePagesTool.Name) ? [SummarizePagesTool.Create(client, results, services)] : [];
            return new AssistantToolSession(client, server, running, stop,
                [.. tools.Select(tool => descriptions.GetValueOrDefault(tool.Name) is { } text ? tool.WithDescription(text) : tool), .. extra], results, proposal);
        }
        catch
        {
            await StopAsync(server, running, stop);
            throw;
        }
    }

    /// <summary>The tool's declared scope; null (never offered) for none or an unknown one.</summary>
    private static string? ScopeOf(McpServerTool tool)
        => tool.Metadata.OfType<McpToolScopeAttribute>().FirstOrDefault()?.Scope is { } scope && ApiScopes.All.Contains(scope) ? scope : null;

    /// <summary>
    /// The assistant's replacement of <see cref="McpToolScopes.CallFilter"/> (spec §4.2), without its token policy: only an offered tool
    /// runs, a write of a turn goes to <see cref="WriteCallAsync"/>, a write of a confirmed proposal (<paramref name="proposal"/> null,
    /// <see cref="ExecuteAsync"/>) runs, the working version of a page only as <see cref="WorkingTextRefusalAsync"/>
    /// allows (it is an author's unpublished text, which a cloud chat profile may receive only with a separate permission), and every refusal reaches the model
    /// as a tool error it can read (<see cref="McpErrors"/>). A result longer than <see cref="OutputCap"/> is cut; what it returned is
    /// noted in <paramref name="results"/> before the cut.
    /// </summary>
    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Call(McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        IReadOnlyDictionary<string, string> scopes, AssistantToolResults results, ProposalSlot? proposal)
        => async (request, ct) =>
        {
            if (!scopes.TryGetValue(request.Params?.Name ?? "", out var scope))
            {
                return McpErrors.Error(404, "tool_not_offered", "Tento nástroj asistent v tomto tahu nemá k dispozici.");
            }
            if (scope != ApiScopes.Read)
            {
                return proposal is null ? await ApprovedCallAsync(next, request, ct) : await WriteCallAsync(request, proposal, ct);
            }
            var arguments = request.Params?.Arguments;
            if (arguments is not null && arguments.TryGetValue("version", out var version)
                && version.ValueKind == JsonValueKind.String && version.GetString() == "working"
                && await WorkingTextRefusalAsync(request.Services!, ct) is { } refusal)
            {
                return McpErrors.Error(403, "working_version_unavailable", refusal);
            }
            // The projector reads show an editor the working rows: under the same gate, without it they read as any reader.
            using var published = request.Params!.Name.StartsWith("projector_", StringComparison.Ordinal)
                && await WorkingTextRefusalAsync(request.Services!, ct) is not null ? ProjectorPublishedReads.Use() : null;
            try
            {
                var result = await next(request, ct);
                if (result.IsError != true)
                {
                    results.Observe(request.Params!.Name, result.StructuredContent);
                }
                return Cap(result);
            }
            catch (ApiException error)
            {
                return McpErrors.From(error);
            }
        };

    /// <summary>
    /// Spec §7.1, S1: whether this call may give the model a page's working version, decided per call from the current configuration
    /// (the admin may change it mid-conversation). The working text goes to the profile <c>assistant.agent</c> runs on, so
    /// <c>assistant.working-text</c> must be available and allowed to send <see cref="AiInputClass.WorkingText"/> to that very profile
    /// (a company profile always, a cloud one only with <c>cloudWorkingText</c>). Whether the user may edit the page stays the handler's
    /// (a reader gets its 404). Null when allowed, otherwise the Czech reason for the model.
    /// </summary>
    private static async ValueTask<string?> WorkingTextRefusalAsync(IServiceProvider services, CancellationToken ct)
    {
        var snapshot = await services.GetRequiredService<AiConfigurationCache>().GetAsync(ct);
        if (snapshot.ProfileFor(AiTaskCatalog.AssistantAgent) is { } profile
            && !AiCapabilities.MaySendInput(snapshot, AiTaskCatalog.Find(AiTaskCatalog.AssistantWorkingText)!, profile))
        {
            return "Pracovní verzi tento profil AI číst nesmí: běží v cloudu a správce mu rozepsaný text neposílá. Povolit to může v Administrace → AI → Funkce. " +
                "Publikovanou verzi přečteš bez parametru version.";
        }
        return services.GetRequiredService<AiCapabilities>().Allows(snapshot, AiTaskCatalog.AssistantWorkingText)
            ? null
            : "Čtení pracovních verzí asistentem je vypnuté; zapnout ho může správce v Administrace → AI → Funkce. Publikovanou verzi přečteš bez parametru version.";
    }

    /// <summary>
    /// THE write seam (D4, WP-20): every call of an offered draft or publish tool from the agent loop ends here and never runs. A tool
    /// with an <see cref="AiProposalShape"/> becomes the turn's proposal (<see cref="AssistantProposals.DraftAsync"/>: the preview is
    /// built by the server under the user's rights) and the model reads that it waits for the user; any other write tool is refused
    /// with <see cref="ConfirmationRequiredCode"/> until its work package gives it a shape. One proposal per turn (spec §6.2).
    /// </summary>
    private static async Task<CallToolResult> WriteCallAsync(RequestContext<CallToolRequestParams> request, ProposalSlot proposal, CancellationToken ct)
    {
        var name = request.Params!.Name;
        if (AiProposalShape.For(name) is not { } shape)
        {
            return McpErrors.Error(403, ConfirmationRequiredCode,
                "Tento zápis asistent zatím navrhnout neumí. Popiš, co bys změnil; uživatel to udělá sám.");
        }
        if (proposal.Draft is not null)
        {
            return McpErrors.Error(409, "proposal_pending",
                "Nejdřív počkejte na potvrzení předchozího návrhu: v jednom tahu je jen jeden návrh. Všechny změny patří do jednoho volání.");
        }
        var arguments = new JsonObject();
        foreach (var (key, value) in request.Params.Arguments ?? new Dictionary<string, JsonElement>())
        {
            arguments[key] = JsonNode.Parse(value.GetRawText());
        }
        AiProposalDraft? draft;
        CallToolResult? error;
        try
        {
            (draft, error) = await AssistantProposals.DraftAsync(request.Services!, shape, arguments, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Never the exception text: an ORM or driver message can quote statement parameters, which hold tool arguments.
            request.Services!.GetRequiredService<ILogger<AssistantToolSession>>()
                .LogError("Assistant proposal of {Tool} failed ({ErrorType})", name, failure.GetType().Name);
            return McpErrors.Error(500, "internal_error", "Návrh se nepodařilo připravit.");
        }
        if (error is not null)
        {
            return error;
        }
        proposal.Draft = draft;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = AssistantProposals.ProposedText }],
            StructuredContent = JsonSerializer.SerializeToElement(new JsonObject { ["proposal"] = "pending", ["items"] = draft!.Items.Count }),
        };
    }

    /// <summary>A write of a confirmed proposal: the tool runs, an <see cref="ApiException"/> becomes its error, and the attempt is audited
    /// after the result like a write over /mcp (<see cref="McpWriteAudit"/>).</summary>
    private static async Task<CallToolResult> ApprovedCallAsync(McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> request, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await McpLiveWrites.CallAsync(request, () => next(request, ct).AsTask(), ct);
        }
        catch (ApiException error)
        {
            result = McpErrors.From(error);
        }
        await McpWriteAudit.RecordAsync(request.Services!, request.Params!.Name, request.Params, result);
        return result;
    }

    private sealed class ProposalSlot
    {
        public AiProposalDraft? Draft;
    }

    /// <summary>
    /// Spec §12: text plus structured content over <see cref="OutputCap"/> first loses the copy of the text in the structured content
    /// (<see cref="McpText.Result"/> adds it for clients that read only that), which the model reads in the text block anyway. Still
    /// over, a text that fits alone stays whole without the structured content; a longer one becomes one text block, cut with
    /// <see cref="TruncationNote"/>, and no structured content, so the model never reads
    /// structured data the text no longer shows.
    /// </summary>
    internal static CallToolResult Cap(CallToolResult result)
    {
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        var structured = result.StructuredContent is { } value ? value.GetRawText().Length : 0;
        if (text.Length + structured <= OutputCap)
        {
            return result;
        }
        if (result.StructuredContent is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("text", out _)
            && JsonNode.Parse(json.GetRawText()) is JsonObject slim && slim.Remove("text")
            && JsonSerializer.SerializeToElement(slim) is var element && text.Length + element.GetRawText().Length <= OutputCap)
        {
            return new CallToolResult { IsError = result.IsError, Content = result.Content, StructuredContent = element };
        }
        if (text.Length <= OutputCap)
        {
            // The text is whole (with its footer, e.g. the nextCursor of a list cut to the assistant's size): only the structured copy goes.
            return new CallToolResult { IsError = result.IsError, Content = result.Content };
        }
        var keep = Math.Min(text.Length, OutputCap - TruncationNote.Length);
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }
        return new CallToolResult { IsError = result.IsError, Content = [new TextContentBlock { Text = text[..keep] + TruncationNote }] };
    }

    public async ValueTask DisposeAsync()
    {
        await m_client.DisposeAsync();
        await StopAsync(m_server, m_running, m_stop);
    }

    private static async Task StopAsync(McpServer server, Task running, CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }
        await server.DisposeAsync();
        stop.Dispose();
    }
}

/// <summary>One end of an in-memory MCP connection: what it sends, the peer reads, stamped by the peer's <c>stamp</c>.</summary>
internal sealed class InProcessMcpTransport : TransportBase
{
    private readonly Action<JsonRpcMessage>? m_stamp;
    private InProcessMcpTransport? m_peer;

    private InProcessMcpTransport(string name, Action<JsonRpcMessage>? stamp, ILoggerFactory logs) : base(name, logs)
    {
        m_stamp = stamp;
        SetConnected();
    }

    /// <param name="serverStamp">Applied to every message the server end reads.</param>
    public static (InProcessMcpTransport Client, InProcessMcpTransport Server) CreatePair(Action<JsonRpcMessage> serverStamp, ILoggerFactory logs)
    {
        var client = new InProcessMcpTransport("assistant-client", null, logs);
        var server = new InProcessMcpTransport("assistant-server", serverStamp, logs);
        client.m_peer = server;
        server.m_peer = client;
        return (client, server);
    }

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        var peer = m_peer ?? throw new InvalidOperationException("The transport is not paired.");
        peer.m_stamp?.Invoke(message);
        await peer.WriteMessageAsync(message, cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        SetDisconnected();
        return ValueTask.CompletedTask;
    }
}

internal sealed class InProcessClientTransport(InProcessMcpTransport transport) : IClientTransport
{
    public string Name => "atlas-assistant";

    public Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult<ITransport>(transport);
}
