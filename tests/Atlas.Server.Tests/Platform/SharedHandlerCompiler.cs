using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.RuntimeCompiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;

namespace Atlas.Server.Tests.Platform;

/// <summary>
/// Wolverine compiles every handler with Roslyn the first time a host invokes it (tests and Development keep
/// generating code at runtime). Hundreds of test hosts would each compile the same handlers again: that was most of
/// the suite's CPU time and tens of gigabytes of never-unloaded assemblies. Hosts in one test process produce the
/// same source for the same handler, so each distinct source is compiled once per process and its assembly reused.
/// A host whose container generates different code (e.g. an extra service) gets its own compile.
/// </summary>
/// <remarks>
/// Wolverine calls this synchronously on a thread-pool thread while other threads of the same host wait on its handler
/// lock, and every other test asking for the same source waits on the <see cref="Lazy{T}"/>. A compile must therefore
/// never need another pool thread: JasperFx's <see cref="AssemblyGenerator"/> compiles with Roslyn's default
/// <c>concurrentBuild</c>, whose <c>Parallel.For</c> blocks the compiling thread until its queued pool work items run. With
/// one process-wide lock around the compile, every other worker was blocked here or behind it, those items waited at the
/// end of the pool's queue, and the pool starved for 15–45 s. So the compile runs single-threaded on the calling thread,
/// outside any lock, and distinct sources compile in parallel.
/// <para>On the Windows Dev Drive testhost, an in-memory assembly load blocked for 4.5 s per handler while a file load from
/// TEMP took 15 ms. Emit to a per-process file so synchronous loading does not hold every host behind this delay.</para>
/// </remarks>
internal sealed class SharedHandlerCompiler : IAssemblyGenerator
{
    private static readonly ConcurrentDictionary<string, Lazy<Assembly>> s_compiled = new(StringComparer.Ordinal);
    // One metadata reference per assembly file for the whole process: Roslyn's native metadata for the dependency graph
    // is gigabytes when every compile creates its own. References are immutable; only the set grows, under the lock.
    private static readonly Lock s_referencesLock = new();
    private static readonly HashSet<Assembly> s_assemblies = [];
    private static readonly Dictionary<string, MetadataReference> s_references = new(StringComparer.OrdinalIgnoreCase);
    private static MetadataReference[] s_snapshot = [];
    private static readonly CSharpCompilationOptions s_options = new(OutputKind.DynamicallyLinkedLibrary, concurrentBuild: false);
    // The compiled handlers of this build, kept between test processes: compiling the whole graph took the first tests of every
    // process about 30 s (each CI batch, each `dotnet test` while developing), loading it from the files takes a second or two.
    // A file is the assembly of one generated source, named by the source's hash, in a folder named by the build (see BuildStamp):
    // the same source against the same assemblies compiles to the same code. Loaded files stay locked; a later process removes
    // the folders of other builds.
    private static readonly string s_outputRoot = Path.Combine(Path.GetTempPath(), "atlas-test-handlers");
    private static readonly Lazy<string> s_output = new(CreateOutput);
    private readonly Lazy<AssemblyGenerator> m_inner = new(() => new AssemblyGenerator());

    // Awaited before either fixture creates its host: one cold graph is compiled while callers wait asynchronously.
    // Otherwise dozens of hosts block pool workers on Lazy<Assembly> and their own HandlerGraph monitors at once.
    private static readonly Lazy<Task> s_prepared = new(PrepareMeasuredAsync);

    public static Task PrepareAsync() => s_prepared.Value;

    private static Task PrepareMeasuredAsync() => TestCosts.MeasureAsync("handlers prepare", PrepareCoreAsync);

    private static async Task PrepareCoreAsync()
    {
        await using var site = await AtlasHttpSite.CreateUnpreparedAsync("atlas_compile_");
        using var client = site.Client();
        var graph = ((WolverineRuntime)site.Services.GetRequiredService<IWolverineRuntime>()).Handlers;
        foreach (var chain in graph.Chains)
        {
            graph.HandlerFor(chain.MessageType);
        }
    }

    public static void Register(IServiceCollection services) => services.AddSingleton<IAssemblyGenerator, SharedHandlerCompiler>();

    public string? AssemblyName
    {
        get => m_inner.Value.AssemblyName;
        set => m_inner.Value.AssemblyName = value;
    }

    public void ReferenceAssembly(Assembly assembly) => m_inner.Value.ReferenceAssembly(assembly);

    public void ReferenceAssemblyContainingType<T>() => m_inner.Value.ReferenceAssemblyContainingType<T>();

    public Assembly Generate(Action<ISourceWriter> write) => m_inner.Value.Generate(write);

    public Assembly Generate(string code) => m_inner.Value.Generate(code);

    public void Compile(GeneratedAssembly generatedAssembly, IServiceVariableSource? services = null) => Compile(generatedAssembly, services, out _);

    public void Compile(GeneratedAssembly generatedAssembly, IServiceVariableSource? services, out string code)
    {
        // Same references as JasperFx's AssemblyGenerator.Compile; GenerateCode arranges frames, so it runs exactly once.
        var generateStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        var source = generatedAssembly.GenerateCode(services);
        TestCosts.Add("handler source", generateStarted);
        code = source;
        var assembly = s_compiled.GetOrAdd(source, key => new Lazy<Assembly>(() => Emit(key, References(
            new[] { typeof(AssemblyGenerator).Assembly, typeof(Task).Assembly, typeof(object).Assembly, typeof(Enumerable).Assembly }
                .Concat(generatedAssembly.Rules.Assemblies)
                .Concat(generatedAssembly.AllReferencedAssemblies())
                .Concat(generatedAssembly.GeneratedTypes.SelectMany(type => type.AssemblyReferences())))))).Value;
        generatedAssembly.AttachAssembly(assembly);
    }

    /// <summary>Adds the assemblies and everything they reference to the process-wide set and returns all references so far:
    /// the same growing superset the shared JasperFx generator kept, only no longer locked while compiling.</summary>
    private static MetadataReference[] References(IEnumerable<Assembly> assemblies)
    {
        lock (s_referencesLock)
        {
            var added = false;
            foreach (var assembly in assemblies)
            {
                added |= Add(assembly);
            }
            if (added)
            {
                s_snapshot = s_references.Values.ToArray();
            }
            return s_snapshot;
        }
    }

    private static bool Add(Assembly assembly)
    {
        if (!s_assemblies.Add(assembly) || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) || s_references.ContainsKey(assembly.Location)
            || assembly.Location.StartsWith(s_outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        s_references.Add(assembly.Location, MetadataReference.CreateFromFile(assembly.Location));
        foreach (var name in assembly.GetReferencedAssemblies())
        {
            try
            {
                Add(Assembly.Load(name));
            }
            catch (FileNotFoundException)
            {
                // As in JasperFx: a reference that does not load (e.g. a compile-time-only facade) is left out.
            }
        }
        return true;
    }

    private static Assembly Emit(string code, MetadataReference[] references)
    {
        var name = "AtlasHandlers_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)))[..32];
        var path = Path.Combine(s_output.Value, name + ".dll");
        if (!File.Exists(path))
        {
            var compileStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            using var measured = new Measured("handler compile", compileStarted);
            var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(code)], references, s_options);
            // Written beside and moved into place, so another process never loads half a file.
            var written = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            Microsoft.CodeAnalysis.Emit.EmitResult result;
            using (var file = File.Create(written))
            {
                result = compilation.Emit(file);
            }
            if (!result.Success)
            {
                File.Delete(written);
                var errors = string.Join("\n", result.Diagnostics
                    .Where(x => x.IsWarningAsError || x.Severity == DiagnosticSeverity.Error)
                    .Select(x => x.Id + ": " + x.GetMessage()));
                throw new InvalidOperationException("Compilation failures!\n\n" + errors + "\n\nCode:\n\n" + code);
            }
            try
            {
                File.Move(written, path);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another test process compiled the same source first.
                File.Delete(written);
            }
        }
        return new CustomAssemblyLoadContext().LoadFromAssemblyPath(path);
    }

    private readonly struct Measured(string step, long startedAt) : IDisposable
    {
        public void Dispose() => TestCosts.Add(step, startedAt);
    }

    private static string CreateOutput()
    {
        var output = Path.Combine(s_outputRoot, "build-" + BuildStamp());
        if (Directory.Exists(s_outputRoot))
        {
            foreach (var folder in Directory.EnumerateDirectories(s_outputRoot))
            {
                // Another build's folder that nothing wrote for a day (a worktree someone else tests has a younger one), and the
                // per-process folders of earlier versions of this class.
                var ended = int.TryParse(Path.GetFileName(folder).Split('-')[0], out var pid) ? !IsRunning(pid)
                    : Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-1);
                if (!ended || string.Equals(folder, output, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    Directory.Delete(folder, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A file still loaded somewhere; a later process retries.
                }
            }
        }
        Directory.CreateDirectory(output);
        Directory.SetLastWriteTimeUtc(output, DateTime.UtcNow);
        return output;
    }

    /// <summary>Names the build the compiled handlers belong to: the runtime and every assembly beside the tests, by name, size and
    /// time of writing. Any rebuilt assembly gives a new stamp, and with it an empty folder.</summary>
    private static string BuildStamp()
    {
        var files = new DirectoryInfo(AppContext.BaseDirectory).EnumerateFiles("*.dll").OrderBy(file => file.Name, StringComparer.Ordinal)
            .Select(file => $"{file.Name}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        var text = Environment.Version + "\n" + string.Join('\n', files);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not ours to inspect: treat it as running.
            return true;
        }
    }
}
