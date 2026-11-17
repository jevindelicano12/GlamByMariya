namespace GlamByMariya.Core.Entities;

public sealed class AccountSettings
{
    public Guid UserId { get; set; }
    public string? AvatarUrl { get; set; }
    public string VerifiedEmail { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;
    public DateTime? VerifiedAt { get; set; }
}

public sealed record AccountSettingsPage(AppUser User, AccountSettings Preferences);
