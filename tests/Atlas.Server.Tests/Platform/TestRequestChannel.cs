namespace Atlas.Server.Tests.Platform;

/// <summary>The channel a test message runs through; web unless a test sets another. AsyncLocal like <see cref="TestActorHolder"/>.
/// The mcp channel is tested this way until C1 maps /mcp.</summary>
public sealed class TestChannelHolder
{
    private readonly AsyncLocal<RequestChannel?> m_current = new();

    public RequestChannel Current
    {
        get => m_current.Value ?? RequestChannel.Web;
        set => m_current.Value = value;
    }
}

public sealed class TestRequestChannel : IRequestChannel
{
    private readonly TestChannelHolder m_holder;

    public TestRequestChannel(TestChannelHolder holder)
    {
        m_holder = holder;
    }

    public RequestChannel Current => m_holder.Current;
}
