using System.ClientModel;
using System.Net.Sockets;
using System.Text.Json;
using Anthropic.Exceptions;

namespace Atlas.Server.Features.Ai.Providers;

/// <summary>
/// Maps whatever a provider SDK, HttpClient or the pipeline threw to an <see cref="AiOutcome"/> code. Reads only types and
/// status codes, never messages or bodies, so the code is all that survives of an upstream error.
/// </summary>
public static class AiErrorClassifier
{
    /// <param name="callerToken">The caller's token: cancellation by the caller is <see cref="AiOutcome.Canceled"/>, any other
    /// cancellation a timeout (the profile's, or HttpClient's).</param>
    public static string Classify(Exception exception, CancellationToken callerToken)
    {
        if (exception is OperationCanceledException && callerToken.IsCancellationRequested)
        {
            return AiOutcome.Canceled;
        }
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case AiCallException call:
                    return call.Code;
                case AiRedirectRefusedException:
                    return AiOutcome.RedirectRefused;
            }
            if (Status(current) is { } status)
            {
                return FromStatus(status);
            }
        }
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException or TimeoutException:
                    return AiOutcome.Timeout;
                case JsonException or FormatException or AnthropicInvalidDataException:
                    return AiOutcome.InvalidOutput;
                case HttpRequestException or SocketException or IOException or AnthropicIOException:
                    return AiOutcome.Connection;
                case ArgumentException or NotSupportedException:
                    return AiOutcome.BadRequest;
            }
        }
        // Anything else broke while talking to the provider; treat it as the provider being unreachable.
        return AiOutcome.Connection;
    }

    public static string FromStatus(int status) => status switch
    {
        401 or 403 => AiOutcome.Auth,
        404 => AiOutcome.ModelNotFound,
        408 => AiOutcome.Timeout,
        429 => AiOutcome.Http429,
        >= 300 and < 400 => AiOutcome.RedirectRefused,
        >= 400 and < 500 => AiOutcome.BadRequest,
        >= 500 => AiOutcome.Http5xx,
        _ => AiOutcome.InvalidOutput,
    };

    /// <summary>The HTTP status an exception reports; null (or 0, a transport failure) when it has none.</summary>
    private static int? Status(Exception exception)
    {
        var status = exception switch
        {
            ClientResultException client => client.Status,
            AnthropicApiException anthropic => (int)anthropic.StatusCode,
            HttpRequestException { StatusCode: { } code } => (int)code,
            _ => 0,
        };
        return status > 0 ? status : null;
    }
}
