namespace Atlas.Server.Features.AiWriting;

public static class AiWritingModule
{
    public static IServiceCollection AddAiWriting(this IServiceCollection services)
    {
        services.AddScoped<AiWritingRunner>();
        return services;
    }

    public static void MapAiWriting(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assist").RequireAuthorization().RequireRateLimiting("ai-assist");
        group.MapPost("/intents", async (IntentRequest request, AiWritingRunner runner, CancellationToken ct) => Results.Ok(await runner.IntentsAsync(request, ct)));
        group.MapPost("/transform", async (TransformRequest request, AiWritingRunner runner, CancellationToken ct) => Results.Ok(await runner.TransformAsync(request, ct)));
        group.MapPost("/outline", async (OutlineRequest request, AiWritingRunner runner, CancellationToken ct) => Results.Ok(await runner.OutlineAsync(request, ct)));
        group.MapPost("/publish-check", async (PublishedDiffRequest request, AiWritingRunner runner, CancellationToken ct) => Results.Ok(await runner.PublishCheckAsync(request, ct)));
        group.MapPost("/tags", async (PublishedDiffRequest request, AiWritingRunner runner, CancellationToken ct) => Results.Ok(await runner.TagsAsync(request, ct)));
        group.MapPost("/change-summary", async (PublishedDiffRequest request, AiWritingRunner runner, CancellationToken ct) => Results.Ok(await runner.ChangeSummaryAsync(request, ct)));
    }
}
