using Atlas.Server.Features.AiIndex.Commands;
using Atlas.Server.Features.AiIndex.Contracts;
using Atlas.Server.Features.AiIndex.Queries;
using Wolverine;

namespace Atlas.Server.Features.AiIndex;

public static class AiIndexEndpoints
{
    /// <summary>Administrace → AI → Index (plan §10, §24). WikiAdmin only; the handlers decide it like the other AI settings.</summary>
    public static void MapAiIndex(this IEndpointRouteBuilder app)
    {
        var index = app.MapGroup("/api/admin/ai/index").RequireAuthorization();
        index.MapGet("", async (IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IndexStatus>(new GetIndexStatus(), ct)));
        index.MapPost("/generations", async (StartIndexGenerationRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<GenerationView>(new StartIndexGeneration(request), ct)));
        index.MapPost("/generations/{id:guid}/verify", async (Guid id, IndexVerificationRunner runner, CancellationToken ct)
            => Results.Ok(await runner.VerifyAsync(id, ct)));
        index.MapPost("/generations/{id:guid}/activate", async (Guid id, ActivateIndexGenerationRequest request, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IndexStatus>(new ActivateIndexGeneration(id, request), ct)));
        index.MapPost("/generations/{id:guid}/cancel", async (Guid id, IMessageBus bus, CancellationToken ct)
            => Results.Ok(await bus.InvokeAsync<IndexStatus>(new CancelIndexGeneration(id), ct)));
    }
}
