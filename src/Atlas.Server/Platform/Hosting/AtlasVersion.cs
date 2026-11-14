using System.Reflection;

namespace Atlas.Server.Platform.Hosting;

/// <summary>
/// The version of the running server: "&lt;release&gt;+&lt;commit&gt;". A release image sets both
/// (Dockerfile: Version and SourceRevisionId); a local build reports the Directory.Build.props default.
/// </summary>
public static class AtlasVersion
{
    public static string Current { get; } = typeof(AtlasVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
