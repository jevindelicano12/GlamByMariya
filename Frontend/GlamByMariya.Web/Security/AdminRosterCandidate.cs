using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

namespace GlamByMariya.Web.Security;

// Explicit candidate only; normal startup does not register this read path.
public static class AdminRosterCandidateRegistration
{
    public static IServiceCollection AddAdminRosterCandidate(this IServiceCollection services)
    {
        services.AddSingleton<IAdminRosterReadSessionFactory>(sp =>
            new PostgresAdminRosterReadSessionFactory(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<AdminRosterService>();
        return services;
    }
}
