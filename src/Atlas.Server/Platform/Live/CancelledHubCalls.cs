using Microsoft.AspNetCore.SignalR;

namespace Atlas.Server.Platform.Live;

/// <summary>
/// A hub call cancelled because its connection closed (a tab closed or a browser went away mid JoinView or SendAwareness) is no
/// failure: it is logged at Debug instead of SignalR's error with a stack trace (COL2-13), and nobody is left to answer. A HubException
/// is the hub's answer to its caller (a refused write, the generation fence, a snapshot pushed around a role handover): a warning
/// without a stack trace. Every other exception is logged here as an Error, since SignalR's own line is filtered out (LiveModule).
/// </summary>
public sealed class CancelledHubCalls : IHubFilter
{
    private readonly ILogger<CancelledHubCalls> m_log;

    public CancelledHubCalls(ILogger<CancelledHubCalls> log)
    {
        m_log = log;
    }

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocationContext);
        }
        catch (OperationCanceledException) when (invocationContext.Context.ConnectionAborted.IsCancellationRequested)
        {
            m_log.LogDebug("Hub method {Method} was cancelled: its connection closed", invocationContext.HubMethodName);
            return null;
        }
        catch (HubException error)
        {
            m_log.LogWarning("Hub method {Method} refused: {Message}", invocationContext.HubMethodName, error.Message);
            throw;
        }
        catch (Exception error)
        {
            m_log.LogError(error, "Hub method {Method} failed", invocationContext.HubMethodName);
            throw;
        }
    }
}
