using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;

namespace GlamByMariya.Web.Security;

/// <summary>Shared application cookie registration and local account validation.</summary>
public static class StudioCookieAuthentication
{
    internal static bool HasDurableValidation(CookieAuthenticationOptions options) =>
        options.EventsType == null && options.Events.GetType() == typeof(CookieAuthenticationEvents)
        && options.Events.OnValidatePrincipal == ValidateDurablePrincipalAsync;

    public static IServiceCollection AddStudioCookieAuthentication(
        this IServiceCollection services, bool isDevelopment, bool durableEnforcement = false)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/Account/Login";
                options.AccessDeniedPath = "/Account/AccessDenied";
                if (durableEnforcement) options.SlidingExpiration = false;
                if (!isDevelopment)
                {
                    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                    options.Cookie.SameSite = SameSiteMode.Lax;
                    options.Cookie.HttpOnly = true;
                }
                options.Events.OnValidatePrincipal = durableEnforcement ? ValidateDurablePrincipalAsync : ValidatePrincipalAsync;
            });
        return services;
    }

    private static async Task ValidateDurablePrincipalAsync(CookieValidatePrincipalContext context)
    {
        // Missing services and repository failures must never fall back to old cookies.
        try
        {
            var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            var guard = context.HttpContext.RequestServices.GetService<VersionedSessionAuthorization>();
            if (context.HttpContext.RequestServices.GetService<IStudioStore>()?.IsPersistent == true &&
                CustomerAuthIntegrationProfile.AllowedEmail(configuration, context.Principal?.FindFirstValue(ClaimTypes.Email)) &&
                guard != null && await guard.ValidateCookie(context.Principal, context.HttpContext.RequestAborted)) return;
        }
        catch (Exception) { }
        context.RejectPrincipal();
    }

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
        {
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        var subjects = principal?.FindAll(DurableSessionAuthorization.SubjectClaim).ToArray() ?? [];
        var provider = context.HttpContext.RequestServices.GetService<SupabaseAuthService>();
        if (provider == null) { context.RejectPrincipal(); return Task.CompletedTask; }
        var providerConfigured = provider.IsConfigured;
        if (providerConfigured)
        {
            if (store.IsPersistent || store is not IVerifiedProviderCustomerStore bindings || subjects.Length != 1 ||
                !Guid.TryParseExact(subjects[0].Value, "D", out var subject) || subject == Guid.Empty ||
                bindings.FindVerifiedCustomer(subject, emails[0].Value)?.Id != accountId)
                context.RejectPrincipal();
        }
        else if (subjects.Length != 0) context.RejectPrincipal();

        return Task.CompletedTask;
    }
}
