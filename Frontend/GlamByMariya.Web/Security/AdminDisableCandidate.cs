using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

namespace GlamByMariya.Web.Security;

// Explicit candidate only; normal startup does not register this transaction path.
public static class AdminDisableCandidate
{
    public static IServiceCollection AddAdminDisableCandidate(this IServiceCollection services)
    {
        services.AddSingleton<IAccountAuthorizationTransactionFactory>(sp =>
            new PostgresAccountAuthorizationTransactionFactory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton(sp => new AccountAuthorizationTransactionService(
            sp.GetRequiredService<IAccountAuthorizationTransactionFactory>(), TimeProvider.System));
        return services;
    }
}
