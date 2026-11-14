using System.ComponentModel;
using ModelContextProtocol.Server;
using Atlas.Server.Features.Mcp;
using Atlas.Server.Features.PersonalApi;

namespace Atlas.Server.Tests.Mcp;

/// <summary>A draft-scope tool for the scope filter tests until the draft tools (C2) exist; registered only by tests.</summary>
[McpServerToolType]
public sealed class McpProbeTools
{
    public const string DraftProbe = "atlas_test_draft_probe";
    public const string Undeclared = "atlas_test_undeclared";

    [McpServerTool(Name = DraftProbe, Destructive = false)]
    [McpToolScope(ApiScopes.Write)]
    [Description("Test probe that needs the write scope.")]
    public static string Draft() => "draft probe ran";

    /// <summary>No McpToolScope: never listed, never run.</summary>
    [McpServerTool(Name = Undeclared, Destructive = false)]
    [Description("Test probe without a declared scope.")]
    public static string Unscoped() => "undeclared probe ran";
}
