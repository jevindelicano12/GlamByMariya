using System.Security.Claims;

namespace GlamByMariya.Core.Entities;

// Canonical role names. Stored in makeup.roles / mvc_roles (Customer, Admin)
// and admin_users.role (Admin, SuperAdmin). Never accept a role from browser input.
public static class Roles
{
    public const string Customer = "Customer";
    public const string Admin = "Admin";
    public const string SuperAdmin = "SuperAdmin";

    public static bool IsStaff(ClaimsPrincipal user) =>
        user.IsInRole(Admin) || user.IsInRole(SuperAdmin);

    public static bool IsSuperAdmin(ClaimsPrincipal user) => user.IsInRole(SuperAdmin);
}

// Single-use administrator invitation. The plaintext token exists only in the
// invitation email; only its SHA-256 hash is stored.
public sealed class AdminInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public Guid InvitedBy { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
