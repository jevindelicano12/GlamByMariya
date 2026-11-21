using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;

namespace GlamByMariya.Web.Security;

/// <summary>Shared application cookie registration and local account validation.</summary>
public static class StudioCookieAuthentication
{
    public static IServiceCollection AddStudioCookieAuthentication(this IServiceCollection services, bool isDevelopment)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Account/Login";
                options.AccessDeniedPath = "/Account/AccessDenied";
                if (!isDevelopment)
                {
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.HttpOnly = true;
                }
                options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
            });
        return services;
    }

    // Every request re-checks the account: it must still exist, be active, keep the same
    // role, and keep the same password hash. Disabling an account or changing its password
    // therefore ends all of its existing sessions on their next request.
    public static Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        var ids = principal?.FindAll(ClaimTypes.NameIdentifier).ToArray() ?? [];
        var emails = principal?.FindAll(ClaimTypes.Email).ToArray() ?? [];
        var roles = principal?.FindAll(ClaimTypes.Role).ToArray() ?? [];
        var versions = principal?.FindAll("account_version").ToArray() ?? [];

        if (ids.Length != 1 || emails.Length != 1 || roles.Length != 1 || versions.Length != 1
            || !Guid.TryParseExact(ids[0].Value, "D", out var accountId)
            || !string.Equals(accountId.ToString(), ids[0].Value, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(emails[0].Value)
            || string.IsNullOrWhiteSpace(roles[0].Value)
            || string.IsNullOrWhiteSpace(versions[0].Value))
        {
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        var store = context.HttpContext.RequestServices.GetRequiredService<IStudioStore>();
        AppUser? user;
        if (store is PostgresStudioStore database)
        {
            user = database.FindUser(emails[0].Value);
        }
        else
        {
            lock (store.Gate)
                user = store.Users.SingleOrDefault(candidate => candidate.Id == accountId);
        }

        if (user == null || !user.IsActive || user.Id != accountId
            || !string.Equals(user.Role, roles[0].Value, StringComparison.Ordinal)
            || !string.Equals(AccountSession.Version(user.PasswordHash), versions[0].Value, StringComparison.Ordinal))
            context.RejectPrincipal();
        return Task.CompletedTask;
    }
}
