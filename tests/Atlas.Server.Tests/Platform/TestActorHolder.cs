namespace Atlas.Server.Tests.Platform;

/// <summary>The actor a test message runs as. AsyncLocal, not a plain field: concurrent probes each set their own actor
/// and the value flows into the awaited invocation without leaking to sibling tasks.</summary>
public sealed class TestActorHolder
{
    private readonly AsyncLocal<UserAccount?> m_current = new();

    public UserAccount? Current
    {
        get => m_current.Value;
        set => m_current.Value = value;
    }
}
