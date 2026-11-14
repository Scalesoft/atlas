namespace Atlas.Server.Features.ApiActivity;

/// <summary>Marks an /api/v1 write route as audited, with its operation. Read by the filter and by the refused-scope audit.</summary>
public sealed record ApiWriteAudit(string Operation);

/// <summary>A write's answer that is not the page itself but names the page and the version it left (Projector's block commands).</summary>
public interface IPageVersionResult
{
    Guid PageId { get; }
    int Version { get; }
}

/// <summary>
/// Audits every attempt of a write route of /api/v1 after its result: ok with the resulting page version, or the outcome code of the
/// ApiException (or of the status) that refused it. The handler's transaction is already committed or rolled back by then, so the
/// audit row has its own. A refused scope never reaches a filter; <see cref="ApiWriteAuthorizationAudit"/> records it. Neither does a
/// rate-limited request (429) or a body that failed to bind (400 before the filters run).
/// </summary>
public sealed class ApiWriteAuditFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (http.GetEndpoint()?.Metadata.GetMetadata<ApiWriteAudit>() is not { } audit)
        {
            return await next(context);
        }
        var pageId = RoutePage(http);
        // The web's create and the personal API's Markdown create: a refused create is audited with the space it addressed.
        var spaceId = context.Arguments.Select(argument => argument switch
        {
            CreatePageRequest request => request.SpaceId,
            ApiCreatePageRequest request => request.SpaceId,
            _ => (Guid?)null
        }).FirstOrDefault(id => id is not null);
        object? result;
        try
        {
            result = await next(context);
        }
        catch (ApiException exception)
        {
            await Recorder(http).RecordAsync(audit.Operation, pageId, spaceId, ApiActivityOutcomes.From(exception), null);
            throw;
        }
        catch (Exception exception)
        {
            var cancelled = exception is OperationCanceledException && http.RequestAborted.IsCancellationRequested;
            await Recorder(http).RecordAsync(audit.Operation, pageId, spaceId, cancelled ? ApiActivityOutcomes.Cancelled : ApiActivityOutcomes.Error, null);
            throw;
        }
        int? version = null;
        if (result is IValueHttpResult { Value: PageDto page })
        {
            (pageId, spaceId, version) = (page.Id, page.SpaceId, page.Version);
        }
        else if (result is IValueHttpResult { Value: IPageVersionResult written })
        {
            (pageId, version) = (written.PageId, written.Version);
        }
        var outcome = result is IStatusCodeHttpResult { StatusCode: >= 400 and var status } ? ApiActivityOutcomes.ForStatus(status) : ApiActivityOutcomes.Ok;
        await Recorder(http).RecordAsync(audit.Operation, pageId, spaceId, outcome, version);
        return result;
    }

    /// <summary>The page a route addresses: {id} on page routes, {pageId} on the attachment upload.</summary>
    internal static Guid? RoutePage(HttpContext http)
        => (http.Request.RouteValues.TryGetValue("id", out var id) ? id : http.Request.RouteValues.GetValueOrDefault("pageId")) is string value
            && Guid.TryParse(value, out var pageId) ? pageId : null;

    private static ApiActivityAudit Recorder(HttpContext http) => http.RequestServices.GetRequiredService<ApiActivityAudit>();
}
