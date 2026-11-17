using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

// Inactive until the durable session path and all security writers are reviewed.
public sealed class PostgresVersionedAccountSecurityProvider : IVersionedAccountSecurityProvider
{
    private readonly PostgresAccountSecurityReadSource source;

    public PostgresVersionedAccountSecurityProvider(IPostgresAccountSecurityQuery query, string schema)
        => source = new PostgresAccountSecurityReadSource(query, schema);

    public async Task<VersionedAccountSecurity?> LoadAsync(Guid accountId, CancellationToken ct)
    {
        var (read, generation) = await source.ReadVersionedAsync(accountId, ct);
        var provider = new RepositoryAccountSecuritySnapshotProvider(
            new SnapshotReadSource(read), SessionStoreKind.PublicPostgres, SessionAuthenticationKind.Supabase);
        var account = await provider.LoadAsync(accountId, ct);
        return account == null ? null : new(account, generation);
    }

    private sealed class SnapshotReadSource(AccountSecurityRead read) : IAccountSecurityReadSource
    {
        public Task<AccountSecurityRead> ReadAsync(Guid accountId, CancellationToken cancellationToken)
            => Task.FromResult(read);
    }
}
