using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

namespace GlamByMariya.Web.Security;

// Explicit candidate only; normal startup must retain the durable-authentication lock.
public static class VersionedAuthenticationCandidate
{
    public static IServiceCollection AddVersionedAuthenticationCandidate(this IServiceCollection services)
    {
        services.AddSingleton<IPostgresAccountSecurityQuery>(sp =>
            new PostgresAccountSecurityQuery(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IVersionedAccountSecurityProvider>(sp =>
            new PostgresVersionedAccountSecurityProvider(sp.GetRequiredService<IPostgresAccountSecurityQuery>(), "public"));
        services.AddSingleton<VersionedSessionAuthorization>();
        services.AddAdminRosterCandidate();
        services.AddAdminDisableCandidate();
        return services;
    }
}
