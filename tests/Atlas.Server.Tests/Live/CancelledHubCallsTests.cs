using System.Security.Claims;
using Atlas.Server.Platform.Live;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Atlas.Server.Tests.Live;

/// <summary>COL2-13: a hub call cancelled by its closed connection is logged at Debug, not as SignalR's error.</summary>
public sealed class CancelledHubCallsTests
{
    [Fact]
    public async Task A_call_cancelled_by_its_closed_connection_is_quiet_and_anything_else_still_fails()
    {
        var log = new Recorded();
        var filter = new CancelledHubCalls(log);
        using var closed = new CancellationTokenSource();
        await closed.CancelAsync();

        Assert.Null(await filter.InvokeMethodAsync(Invocation(closed.Token), _ => throw new TaskCanceledException()));
        Assert.Equal([LogLevel.Debug], log.Levels);

        // Open connection: a cancellation is something else (a timeout) and stays a failure, logged as an Error here (SignalR's own line
        // is filtered out); a HubException is the hub's answer (campaign 2026-11-05 F6), a warning; both still reach the caller.
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await filter.InvokeMethodAsync(Invocation(CancellationToken.None), _ => throw new OperationCanceledException()));
        await Assert.ThrowsAsync<HubException>(async () => await filter.InvokeMethodAsync(Invocation(closed.Token), _ => throw new HubException("forbidden")));
        Assert.Equal("done", await filter.InvokeMethodAsync(Invocation(CancellationToken.None), _ => ValueTask.FromResult<object?>("done")));
        Assert.Equal([LogLevel.Debug, LogLevel.Error, LogLevel.Warning], log.Levels);
    }

    private static HubInvocationContext Invocation(CancellationToken aborted) =>
        new(new Caller(aborted), new EmptyServices(), new Probe(), typeof(Probe).GetMethod(nameof(Probe.Join))!, []);

    private sealed class Probe : Microsoft.AspNetCore.SignalR.Hub
    {
        public Task Join() => Task.CompletedTask;
    }

    private sealed class Caller(CancellationToken aborted) : HubCallerContext
    {
        public override string ConnectionId => "c1";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => aborted;
        public override void Abort() { }
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class Recorded : ILogger<CancelledHubCalls>
    {
        public List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }
}
