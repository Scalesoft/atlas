namespace Atlas.Server.Features.Ai.Providers;

/// <summary>
/// The outbound HTTP of every AI provider: the named client <see cref="ClientName"/> never follows a redirect, sends no cookies
/// and no default or proxy credentials, and has no client-wide timeout (each call carries its profile's). A redirect is an
/// error (<see cref="AiOutcome.RedirectRefused"/>), so a key never reaches a host it was not entered for (spec RAG §3). Logs
/// carry the host and the status code only: the client's default request loggers are removed (they would log URLs and, at
/// trace level, headers).
/// </summary>
public static class AiHttp
{
    public const string ClientName = "atlas-ai";

    public static IServiceCollection AddAiHttp(this IServiceCollection services)
    {
        services.AddTransient<RedirectRefusingHandler>();
        services.AddHttpClient(ClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                Credentials = null,
                DefaultProxyCredentials = null,
                PreAuthenticate = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            .AddHttpMessageHandler<RedirectRefusingHandler>()
            .RemoveAllLoggers();
        return services;
    }
}

/// <summary>Turns any 3xx answer into <see cref="AiRedirectRefusedException"/> before an SDK can act on it.</summary>
internal sealed class RedirectRefusingHandler(ILogger<RedirectRefusingHandler> logger) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var status = (int)response.StatusCode;
        if (status is < 300 or > 399)
        {
            return response;
        }
        response.Dispose();
        var host = request.RequestUri?.Host ?? "(none)";
        // Never the Location: it is the upstream's text and may carry anything.
        logger.LogWarning("AI provider at {Host} answered with redirect {Status}; Atlas does not follow redirects", host, status);
        throw new AiRedirectRefusedException(host, status);
    }
}

/// <summary>An AI provider answered with a redirect. Holds the request host and the status, never the target.</summary>
public sealed class AiRedirectRefusedException(string host, int status)
    : HttpRequestException($"The AI provider at {host} answered with redirect {status}; redirects are not followed.")
{
    public string Host { get; } = host;
    public int Status { get; } = status;
}
