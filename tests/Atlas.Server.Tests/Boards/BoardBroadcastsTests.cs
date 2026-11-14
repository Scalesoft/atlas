using Atlas.Server.Features.Boards;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Atlas.Server.Tests.Boards;

/// <summary>COL2-3: the queue of a board's live messages, outside the request.</summary>
public sealed class BoardBroadcastsTests
{
    [Fact]
    public async Task One_boards_messages_go_in_order_a_failing_one_stops_none_and_another_board_never_waits_for_them()
    {
        var queue = new BoardBroadcasts(NullLogger<BoardBroadcasts>.Instance);
        Guid board = Guid.NewGuid(), other = Guid.NewGuid();
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        // Enqueue returns while the first send hangs.
        queue.Enqueue(board, async () => { await slow.Task; order.Add("1"); });
        queue.Enqueue(board, () => throw new InvalidOperationException("the hub refused"));
        queue.Enqueue(board, () => { order.Add("3"); return Task.CompletedTask; });
        var elsewhere = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue(other, () => { elsewhere.SetResult(); return Task.CompletedTask; });

        await elsewhere.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(order);
        slow.SetResult();
        await queue.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["1", "3"], order);
        // Drained: a new message runs at once.
        queue.Enqueue(board, () => { order.Add("4"); return Task.CompletedTask; });
        await queue.IdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["1", "3", "4"], order);
    }
}
