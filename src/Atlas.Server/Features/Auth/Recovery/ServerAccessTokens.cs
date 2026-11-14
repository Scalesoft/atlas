using Atlas.Server.Features.Auth.Builtin;
using Atlas.Server.Features.Auth.Contracts;
using Atlas.Server.Features.Auth.Queries;
using Atlas.Server.Features.Auth.Recovery.Commands;
using System.Security.Cryptography;
using System.Text;
using Wolverine;

namespace Atlas.Server.Features.Auth.Recovery;

/// <summary>
/// Server capabilities that exist before and outside any request: the installation token and the
/// administrator recovery token. Files, the one-shot log and the bootstrap gate are singleton state;
/// every database step is a message in its own scope and transaction. The three steps of RecoverAsync
/// must stay separate transactions — they take the same advisory lock in sequence, never nested.
/// </summary>
public sealed class ServerAccessTokens
{
    private readonly IServiceScopeFactory m_scopes;
    private readonly ServerTokenFiles m_files;
    private readonly IConfiguration m_configuration;
    private readonly ILogger<ServerAccessTokens> m_logger;
    private readonly BuiltinAuthenticationOptions m_builtinOptions;
    private readonly BuiltinPasswordPolicy m_passwordPolicy;
    private readonly SemaphoreSlim m_bootstrapGate = new(1, 1);
    private bool m_bootstrapLogged;

    public ServerAccessTokens(
        IServiceScopeFactory scopes, ServerTokenFiles files, IConfiguration configuration,
        ILogger<ServerAccessTokens> logger, BuiltinAuthenticationOptions builtinOptions,
        BuiltinPasswordPolicy passwordPolicy)
    {
        m_scopes = scopes;
        m_files = files;
        m_configuration = configuration;
        m_logger = logger;
        m_builtinOptions = builtinOptions;
        m_passwordPolicy = passwordPolicy;
    }

    public SetupTokenSource SetupSource => new(string.IsNullOrEmpty(m_configuration["Setup:BootstrapToken"]) ? "file" : "configuration", m_files.BootstrapPath,
        $"Instalační token je ve výstupu serveru (Rider/Aspire: log prostředku atlas-service) a v souboru {m_files.BootstrapPath} na serveru. Zkopírujte pouze hodnotu tokenu. Po dokončení instalace se již nevypisuje.");

    public static string GenerateToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    internal static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public async Task<string?> EnsureBootstrapAsync(CancellationToken ct = default)
    {
        await m_bootstrapGate.WaitAsync(ct);
        try
        {
            if ((await InvokeAsync<WorkspaceState>(new GetWorkspaceState(), ct)).Configured)
            {
                return null;
            }
            var token = await m_files.BootstrapAsync(m_configuration["Setup:BootstrapToken"], ct);
            if (!m_bootstrapLogged)
            {
                // Explicit product requirement: server operators obtain this capability from
                // the process log. Never place it in request logging or public status DTOs.
                m_logger.LogWarning("Atlas first installation token: {BootstrapToken}. Token file: {TokenFile}", token, m_files.BootstrapPath);
                m_bootstrapLogged = true;
            }
            return token;
        }
        finally
        {
            m_bootstrapGate.Release();
        }
    }

    public async Task ValidateBootstrapAsync(string? supplied, CancellationToken ct)
    {
        var expected = await EnsureBootstrapAsync(ct) ?? throw new ApiException(409, "Wiki již byla nastavena.");
        if (supplied is null || supplied.Length > 4096 || !CryptographicOperations.FixedTimeEquals(Hash(expected), Hash(supplied)))
        {
            throw new ApiException(403, "Neplatný instalační token. Najdete jej ve výstupu serveru a v instalačním souboru uvedeném nad formulářem.");
        }
    }

    public async Task<RecoveryStatus> GetRecoveryStatusAsync(CancellationToken ct = default)
    {
        var enabled = m_builtinOptions.Enabled;
        var current = await EnsureRecoveryAsync(ct);
        // The status is anonymous: it names the file, not where the server keeps it (SEC-A-9); docs/administrator-recovery.md says that.
        var file = Path.GetFileName(m_files.RecoveryPath);
        return new RecoveryStatus(current is not null, enabled, file, current is null
            ? "Obnova je dostupná po prvním nastavení. Pokud chybí účet pro obnovu, nastavte na serveru Recovery:Username na uživatelské jméno existujícího účtu a restartujte."
            : $"Na serveru otevřete soubor {file} v datovém adresáři Atlasu (Security:DataPath) a zkopírujte pole Token. Token platí sedm dní, po použití se vytvoří nový. Obnova svázanému účtu nastaví heslo, obnoví práva správce a ihned vás přihlásí, i když je běžné místní přihlášení vypnuté.");
    }

    public async Task<RecoveryTokenFile?> EnsureRecoveryAsync(CancellationToken ct = default)
    {
        var result = await InvokeAsync<RecoveryTokenResult>(new EnsureRecoveryToken(), ct);
        if (result.Issued && result.File is { } issued)
        {
            // Logged after commit, and only where it is: whoever reads the logs must not become an administrator (SEC-A-7).
            m_logger.LogWarning("Atlas administrator recovery token issued. Account: {Username}. Valid until: {ExpiresAt:u}. Token file: {TokenFile}", issued.Username, issued.ExpiresAt, m_files.RecoveryPath);
        }
        return result.File;
    }

    public async Task<UserAccount> RecoverAsync(RecoverAdministrator request, CancellationToken ct)
    {
        m_passwordPolicy.Validate(request.NewPassword);
        if (request.Token is null || request.Token.Length != 64)
        {
            throw InvalidRecovery();
        }
        if (await EnsureRecoveryAsync(ct) is null)
        {
            throw InvalidRecovery();
        }
        var recovered = await InvokeAsync<UserAccount>(new RedeemRecoveryToken(request.Token, request.NewPassword), ct);
        // Password reset has committed. A filesystem failure must not report that it failed;
        // initialization/status retries generation of the replacement recovery capability.
        try
        {
            await EnsureRecoveryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            m_logger.LogError("Recovery succeeded, but replacement token file could not be generated ({ErrorType}). Restart or retry recovery status after fixing server file access.", ex.GetType().Name);
        }
        return recovered;
    }

    private async Task<T> InvokeAsync<T>(object message, CancellationToken ct)
    {
        using var scope = m_scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, ct);
    }

    internal static ApiException InvalidRecovery()
    {
        return new ApiException(403, "Obnovovací token není platný nebo už byl použit. Použijte aktuální soubor na serveru.");
    }
}
