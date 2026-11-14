using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NHibernate;
using NHibernate.Linq;
using OpenIddict.Abstractions;
using Atlas.Server.Features.Auth.OpenIdConnect.Domain;

namespace Atlas.Server.Features.Auth.OpenIdConnect;

// Each operation owns its session. The NH version predicate makes state redemption atomic
// across requests/processes; stale updates must surface as OpenIddict concurrency failures.
// Sanctioned exception from the scoped-session rule: IOpenIddictTokenStore is a generic queryable
// contract whose operations cannot be expressed as messages carrying pure data.
public sealed class NHibernateOidcTokenStore : IOpenIddictTokenStore<OidcStateToken>
{
    private readonly AtlasDatabase m_database;

    public NHibernateOidcTokenStore(AtlasDatabase database)
    {
        m_database = database;
    }

    public async ValueTask CreateAsync(OidcStateToken token, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var transaction = session.BeginTransaction();
        await session.SaveAsync(token, ct);
        await transaction.CommitAsync(ct);
    }

    public async ValueTask UpdateAsync(OidcStateToken token, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var transaction = session.BeginTransaction();
        try
        {
            await session.UpdateAsync(token, ct);
            await transaction.CommitAsync(ct);
        }
        catch (StaleStateException ex)
        {
            throw new OpenIddictExceptions.ConcurrencyException("The state token has already changed.", ex);
        }
    }

    public async ValueTask DeleteAsync(OidcStateToken token, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var transaction = session.BeginTransaction();
        try
        {
            await session.DeleteAsync(token, ct);
            await transaction.CommitAsync(ct);
        }
        catch (StaleStateException ex)
        {
            throw new OpenIddictExceptions.ConcurrencyException("The state token has already changed.", ex);
        }
    }

    public async ValueTask<long> CountAsync(CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        return await session.Query<OidcStateToken>().LongCountAsync(ct);
    }

    public async ValueTask<long> CountAsync<TResult>(Func<IQueryable<OidcStateToken>, IQueryable<TResult>> query, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        return await query(session.Query<OidcStateToken>()).LongCountAsync(ct);
    }

    public async ValueTask<TResult?> GetAsync<TState, TResult>(Func<IQueryable<OidcStateToken>, TState, IQueryable<TResult>> query, TState state, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        return await query(session.Query<OidcStateToken>(), state).FirstOrDefaultAsync(ct);
    }

    public async ValueTask<OidcStateToken?> FindByIdAsync(string id, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        return await session.GetAsync<OidcStateToken>(id, ct);
    }

    public ValueTask<OidcStateToken?> FindByReferenceIdAsync(string id, CancellationToken ct) => GetAsync((q, value) => q.Where(t => t.ReferenceId == value), id, ct);

    public IAsyncEnumerable<OidcStateToken> FindByApplicationIdAsync(string id, CancellationToken ct) => ListAsync((q, value) => q.Where(t => t.ApplicationId == value), id, ct);

    public IAsyncEnumerable<OidcStateToken> FindByAuthorizationIdAsync(string id, CancellationToken ct) => ListAsync((q, value) => q.Where(t => t.AuthorizationId == value), id, ct);

    public IAsyncEnumerable<OidcStateToken> FindBySubjectAsync(string id, CancellationToken ct) => ListAsync((q, value) => q.Where(t => t.Subject == value), id, ct);

    public IAsyncEnumerable<OidcStateToken> FindAsync(string? subject, string? client, string? status, string? type, CancellationToken ct) => ListAsync((q, _) => Filter(q, subject, client, status, type), 0, ct);

    public IAsyncEnumerable<OidcStateToken> ListAsync(int? count, int? offset, CancellationToken ct) => ListAsync((q, _) => q.OrderBy(t => t.Id).Skip(offset ?? 0).Take(count ?? int.MaxValue), 0, ct);

    public async IAsyncEnumerable<TResult> ListAsync<TState, TResult>(Func<IQueryable<OidcStateToken>, TState, IQueryable<TResult>> query, TState state, [EnumeratorCancellation] CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        foreach (var value in await query(session.Query<OidcStateToken>(), state).ToListAsync(ct))
        {
            yield return value;
        }
    }

    public async ValueTask<long> PruneAsync(DateTimeOffset threshold, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var transaction = session.BeginTransaction();
        var now = DateTime.UtcNow;
        var cutoff = threshold.UtcDateTime;
        var count = await session.Query<OidcStateToken>().Where(t => t.CreationDate < cutoff && (t.Status != "valid" || t.ExpirationDate < now)).DeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    public async ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var transaction = session.BeginTransaction();
        var count = await Filter(session.Query<OidcStateToken>(), subject, client, status, type).UpdateAsync(t => new OidcStateToken { Status = "revoked", Version = t.Version + 1 }, ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    public ValueTask<long> RevokeByApplicationIdAsync(string id, CancellationToken ct = default) => RevokeAsync(null, id, null, null, ct);

    public ValueTask<long> RevokeBySubjectAsync(string id, CancellationToken ct = default) => RevokeAsync(id, null, null, null, ct);

    public async ValueTask<long> RevokeByAuthorizationIdAsync(string id, CancellationToken ct)
    {
        using var session = m_database.OpenSession();
        using var transaction = session.BeginTransaction();
        var count = await session.Query<OidcStateToken>().Where(t => t.AuthorizationId == id).UpdateAsync(t => new OidcStateToken { Status = "revoked", Version = t.Version + 1 }, ct);
        await transaction.CommitAsync(ct);
        return count;
    }

    public ValueTask<OidcStateToken> InstantiateAsync(CancellationToken ct) => ValueTask.FromResult(new OidcStateToken());

    public ValueTask<ImmutableDictionary<string, JsonElement>> GetPropertiesAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult(token.Properties is null ? ImmutableDictionary<string, JsonElement>.Empty : JsonSerializer.Deserialize<ImmutableDictionary<string, JsonElement>>(token.Properties)!);

    public ValueTask SetPropertiesAsync(OidcStateToken token, ImmutableDictionary<string, JsonElement> properties, CancellationToken ct)
    {
        token.Properties = JsonSerializer.Serialize(properties);
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetIdAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.Id);

    public ValueTask<string?> GetApplicationIdAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.ApplicationId);

    public ValueTask SetApplicationIdAsync(OidcStateToken token, string? value, CancellationToken ct)
    {
        token.ApplicationId = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetAuthorizationIdAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.AuthorizationId);

    public ValueTask SetAuthorizationIdAsync(OidcStateToken token, string? value, CancellationToken ct)
    {
        token.AuthorizationId = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetPayloadAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.Payload);

    public ValueTask SetPayloadAsync(OidcStateToken token, string? value, CancellationToken ct)
    {
        token.Payload = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetReferenceIdAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.ReferenceId);

    public ValueTask SetReferenceIdAsync(OidcStateToken token, string? value, CancellationToken ct)
    {
        token.ReferenceId = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetStatusAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.Status);

    public ValueTask SetStatusAsync(OidcStateToken token, string? value, CancellationToken ct)
    {
        token.Status = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetSubjectAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.Subject);

    public ValueTask SetSubjectAsync(OidcStateToken token, string? value, CancellationToken ct)
    {
        token.Subject = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<string?> GetTypeAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<string?>(token.Type);

    public ValueTask SetTypeAsync(OidcStateToken token, string? value, CancellationToken ct)
    {
        token.Type = value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DateTimeOffset?> GetCreationDateAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<DateTimeOffset?>(token.CreationDate is { } value ? new DateTimeOffset(value) : null);

    public ValueTask SetCreationDateAsync(OidcStateToken token, DateTimeOffset? value, CancellationToken ct)
    {
        token.CreationDate = value?.UtcDateTime;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DateTimeOffset?> GetExpirationDateAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<DateTimeOffset?>(token.ExpirationDate is { } value ? new DateTimeOffset(value) : null);

    public ValueTask SetExpirationDateAsync(OidcStateToken token, DateTimeOffset? value, CancellationToken ct)
    {
        token.ExpirationDate = value?.UtcDateTime;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DateTimeOffset?> GetRedemptionDateAsync(OidcStateToken token, CancellationToken ct) => ValueTask.FromResult<DateTimeOffset?>(token.RedemptionDate is { } value ? new DateTimeOffset(value) : null);

    public ValueTask SetRedemptionDateAsync(OidcStateToken token, DateTimeOffset? value, CancellationToken ct)
    {
        token.RedemptionDate = value?.UtcDateTime;
        return ValueTask.CompletedTask;
    }

    private static IQueryable<OidcStateToken> Filter(IQueryable<OidcStateToken> query, string? subject, string? client, string? status, string? type)
    {
        if (subject is not null)
        {
            query = query.Where(t => t.Subject == subject);
        }
        if (client is not null)
        {
            query = query.Where(t => t.ApplicationId == client);
        }
        if (status is not null)
        {
            query = query.Where(t => t.Status == status);
        }
        if (type is not null)
        {
            query = query.Where(t => t.Type == type);
        }
        return query;
    }
}
