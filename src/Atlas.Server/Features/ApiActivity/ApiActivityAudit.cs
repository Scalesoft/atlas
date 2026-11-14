using System.Security.Claims;
using Atlas.Server.Features.ApiActivity.Commands;
using Wolverine;

namespace Atlas.Server.Features.ApiActivity;

/// <summary>
/// Records one write attempt of the current personal API request (api on /api/v1, mcp on /mcp) or of the assistant's tool session
/// (assistant, without a token) as <see cref="RecordApiActivity"/>,
/// in its own short transaction. Used by <see cref="ApiWriteAuditFilter"/>, by <see cref="ApiWriteAuthorizationAudit"/> for a refused
/// scope, and by the MCP tool filter (C1–C3). Never throws: a failed audit is logged with the request id and leaves the response as it is.
/// The success itself stays documented by the revision (Channel, ApiTokenId) even if its audit row is lost.
/// </summary>
public sealed class ApiActivityAudit
{
    private const int RequestIdLength = 64;
    private readonly IHttpContextAccessor m_http;
    private readonly IRequestChannel m_channel;
    private readonly IMessageBus m_bus;
    private readonly ILogger<ApiActivityAudit> m_logger;

    public ApiActivityAudit(IHttpContextAccessor http, IRequestChannel channel, IMessageBus bus, ILogger<ApiActivityAudit> logger)
    {
        m_http = http;
        m_channel = channel;
        m_bus = bus;
        m_logger = logger;
    }

    /// <param name="operation">An <see cref="ApiOperations"/> value, or the MCP tool name.</param>
    /// <param name="outcome">ok or a refusal code (<see cref="ApiActivityOutcomes"/>); anything the column refuses is recorded as error.</param>
    public async Task RecordAsync(string operation, Guid? pageId, Guid? spaceId, string outcome, int? resultVersion)
    {
        var context = m_http.HttpContext;
        var requestId = context?.TraceIdentifier;
        try
        {
            var channel = m_channel.Current;
            // The assistant writes as the signed-in user without a token; any other channel is audited only with its token.
            if ((channel.ApiTokenId is null && channel.Channel != ContentChannel.Assistant)
                || !Guid.TryParse(context?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId))
            {
                return;
            }
            // Not the request's token: the audit is written even when the client has gone away.
            await m_bus.InvokeAsync(new RecordApiActivity(actorId, channel.ApiTokenId, channel.Code, operation, pageId, spaceId,
                ApiActivityOutcomes.IsValid(outcome) ? outcome : ApiActivityOutcomes.Error, resultVersion,
                requestId is { Length: > RequestIdLength } ? requestId[..RequestIdLength] : requestId), CancellationToken.None);
        }
        catch (Exception exception)
        {
            m_logger.LogError("API activity audit of request {RequestId} failed ({ErrorType})", requestId, exception.GetType().Name);
        }
    }
}
