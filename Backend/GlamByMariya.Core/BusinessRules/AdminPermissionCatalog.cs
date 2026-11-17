using System.Security.Claims;
using GlamByMariya.Core.Entities;

namespace GlamByMariya.Core.BusinessRules;

// Preparation only: not an authorization policy or a persistent grant store.
public static class AdminPermissionCatalog
{
    public static IReadOnlyList<(string Key, string Label)> Items { get; } = Array.AsReadOnly(new[]
    {
        ("dashboard", "Dashboard"),
        ("appointments", "Appointments and Calendar"),
        ("catalog", "Makeup Services and Add-ons"),
        ("payments", "Payments"),
        ("reports", "Sales Reports"),
        ("business-settings", "Business Settings"),
        ("gallery", "Gallery Management")
    });

    public static bool CanManage(ClaimsPrincipal actor, AppUser target) =>
        actor.Identity?.IsAuthenticated == true && actor.IsInRole(Roles.SuperAdmin) && target.Id != Guid.Empty && target.Role == Roles.Admin &&
        Guid.TryParse(actor.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var actorId) && actorId != Guid.Empty && actorId != target.Id;

    // Consumers must load current grants for every request; missing or invalid grants deny.
    public static bool Has(ClaimsPrincipal actor, AppUser account, string permission, IReadOnlyCollection<string>? grants)
    {
        if (actor.Identity?.IsAuthenticated != true || account.Id == Guid.Empty || !account.IsActive ||
            !Guid.TryParse(actor.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var actorId) || actorId != account.Id ||
            !Items.Any(x => x.Key == permission) || !actor.IsInRole(account.Role)) return false;
        return account.Role == Roles.SuperAdmin || (account.Role == Roles.Admin && grants != null && grants.Contains(permission, StringComparer.Ordinal));
    }
}
