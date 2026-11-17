using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GlamByMariya.Infrastructure.Data;

/// <summary>Balances EF's explicit-open reference count, including exceptions.
/// Does not dispose the context-owned connection or close a caller's lease.</summary>
public sealed class DatabaseConnectionLease : IAsyncDisposable
{
    private DatabaseFacade? database;
    private DatabaseConnectionLease(DatabaseFacade database) => this.database = database;

    public static async Task<DatabaseConnectionLease> OpenAsync(DatabaseFacade database)
    {
        await database.OpenConnectionAsync();
        return new DatabaseConnectionLease(database);
    }

    public async ValueTask DisposeAsync()
    {
        var owned = Interlocked.Exchange(ref database, null);
        if (owned != null) await owned.CloseConnectionAsync();
    }
}
