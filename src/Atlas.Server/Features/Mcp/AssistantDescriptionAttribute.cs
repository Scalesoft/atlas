namespace Atlas.Server.Features.Mcp;

/// <summary>
/// The short Czech description the in-process assistant gives its model for this tool (spec §5.1): one sentence of when to use it and
/// its key arguments, about 200 characters. External /mcp clients keep the longer <see cref="System.ComponentModel.DescriptionAttribute"/>;
/// a tool without this attribute is described to the assistant by that one. Plain text only: MCP knows nothing of the assistant.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AssistantDescriptionAttribute(string text) : Attribute
{
    public string Text { get; } = text;
}
