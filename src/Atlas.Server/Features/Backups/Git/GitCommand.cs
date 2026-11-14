using System.Diagnostics;
using System.Text;

namespace Atlas.Server.Features.Backups.Git;

public sealed record GitResult(int ExitCode, string Output, string Error);

/// <summary>
/// Runs the git CLI (and ssh-keygen) as a child process: arguments are passed without a shell, and neither system nor user
/// configuration, credential helpers, prompts nor an SSH agent take part. Only the ssh and file transports are allowed.
/// </summary>
public sealed class GitCommand
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(10);
    private static readonly string[] Configuration =
    [
        "-c", "core.autocrlf=false", "-c", "core.quotepath=off", "-c", "core.longpaths=true", "-c", "core.fsmonitor=false",
        "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", "-c", "advice.detachedHead=false",
        // A symlink committed to the repository by anyone with write access must never make Atlas read or write outside it.
        "-c", "core.symlinks=false",
        // No automatic gc inside a commit (a timeout would kill it); the service runs it after a push.
        "-c", "gc.auto=0",
        "-c", "user.name=" + GitIdentity.Committer.Name, "-c", "user.email=" + GitIdentity.Committer.Email
    ];

    // Linux: the idle I/O class (-t: run anyway where it cannot be set); the CPU priority is lowered after the start, on every OS.
    private static readonly string? s_ionice = OperatingSystem.IsLinux() && File.Exists("/usr/bin/ionice") ? "/usr/bin/ionice" : null;

    private readonly string m_directory;
    private readonly IReadOnlyDictionary<string, string> m_environment;

    public GitCommand(string directory, IReadOnlyDictionary<string, string> environment)
    {
        m_directory = directory;
        m_environment = environment;
    }

    public string Directory => m_directory;

    /// <summary>
    /// The process environment for one data directory. <paramref name="sshCommand"/> is the complete GIT_SSH_COMMAND (a key file,
    /// the known hosts file and non-interactive options), or null for local repositories only.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Environment(string dataPath, string? sshCommand)
    {
        var globalConfig = Path.Combine(dataPath, "gitconfig");
        if (!File.Exists(globalConfig))
            File.WriteAllText(globalConfig, "");
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = dataPath,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = globalConfig,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_ALLOW_PROTOCOL"] = "ssh:file",
            ["GIT_LITERAL_PATHSPECS"] = "1",
            ["LC_ALL"] = "C",
            ["LANG"] = "C"
        };
        if (sshCommand is not null)
            environment["GIT_SSH_COMMAND"] = sshCommand;
        return environment;
    }

    /// <summary>One argument of GIT_SSH_COMMAND, which git runs through a POSIX shell (also Git for Windows).</summary>
    public static string ShellQuote(string value) => "'" + value.Replace('\\', '/').Replace("'", "'\\''") + "'";

    public static string SshCommand(string keyFile, string knownHostsFile) => string.Join(' ',
        "ssh", "-F", "none", "-i", ShellQuote(keyFile), "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none", "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=accept-new", "-o", "UserKnownHostsFile=" + ShellQuote(knownHostsFile), "-o", "UpdateHostKeys=no",
        "-o", "ConnectTimeout=20", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4", "-o", "LogLevel=ERROR");

    public Task<GitResult> GitAsync(CancellationToken ct, params string[] arguments) => RunGitAsync(arguments, null, null, true, ct);

    public Task<GitResult> RunGitAsync(IEnumerable<string> arguments, string? input, TimeSpan? timeout, bool check, CancellationToken ct)
        => RunAsync("git", Configuration.Concat(arguments), input, timeout, check, ct);

    public async Task<GitResult> RunAsync(string program, IEnumerable<string> arguments, string? input, TimeSpan? timeout, bool check,
        CancellationToken ct, IReadOnlyDictionary<string, string>? extraEnvironment = null)
    {
        var info = new ProcessStartInfo(s_ionice ?? program)
        {
            WorkingDirectory = m_directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (s_ionice is not null)
            foreach (var argument in new[] { "-c3", "-t", program })
                info.ArgumentList.Add(argument);
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        // Inherited git/ssh state (a hook's GIT_DIR, an agent socket, a desktop askpass) must never steer the backup.
        foreach (var name in info.Environment.Keys.ToList())
        {
            if (name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SSH_", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase))
                info.Environment.Remove(name);
        }
        foreach (var (name, value) in m_environment)
            info.Environment[name] = value;
        if (extraEnvironment is not null)
            foreach (var (name, value) in extraEnvironment)
                info.Environment[name] = value;

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException($"Program {program} není na serveru dostupný. Nainstalujte git a OpenSSH klienta.", ex);
        }
        // Backups yield the CPU to the editors (owner, 2026-11-08): nice 10 on Linux, BelowNormal on Windows; git's children inherit it.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(limit.Token);
            var result = new GitResult(process.ExitCode, await output, await error);
            if (check && result.ExitCode != 0)
                throw new GitException(Describe(program, arguments, result));
            return result;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested)
                throw;
            throw new GitException($"Příkaz {Name(program, arguments)} nedoběhl včas.");
        }
    }

    /// <summary>"git push" or "ssh-keygen": enough to tell the failing step, never the arguments (URLs, paths).</summary>
    private static string Name(string program, IEnumerable<string> arguments)
        => program != "git" ? program
            : "git " + (arguments.SkipWhile(x => x == "-c" || x.Contains('=') && !x.StartsWith('-')).FirstOrDefault(x => !x.StartsWith('-')) ?? "");

    private static string Describe(string program, IEnumerable<string> arguments, GitResult result)
    {
        var detail = (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
        if (detail.Length > 600)
            detail = detail[..600] + "…";
        return $"{Name(program, arguments)} skončil kódem {result.ExitCode}" + (detail.Length > 0 ? ": " + detail : ".");
    }
}

public sealed class GitException : Exception
{
    public GitException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record GitIdentity(string Name, string Email)
{
    public static readonly GitIdentity Committer = new("Atlas", "atlas@atlas.invalid");
}
