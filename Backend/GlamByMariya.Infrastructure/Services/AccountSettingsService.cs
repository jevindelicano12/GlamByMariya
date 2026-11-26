using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using System.Globalization;
using Npgsql;

namespace GlamByMariya.Infrastructure.Services;

public sealed class AccountSettingsService(IStudioStore store, IServiceProvider services, IMemoryCache cache)
{
    private PublicMakeupDbContext Db => services.GetRequiredService<PublicMakeupDbContext>();
    public AccountSettings Get(Guid id)
    {
        if (!store.IsPersistent)
        {
            var preferences = cache.GetOrCreate($"preferences:{id}", _ => new AccountSettings { UserId = id })!;
            var avatars = services.GetService<DemoAvatarStore>();
            if (avatars != null)
            {
                lock (store.Gate)
                {
                    var user = store.Users.FirstOrDefault(x => x.Id == id);
                    if (user != null && avatars.Supports(user)) preferences.AvatarUrl = avatars.Has(user) ? "/Account/DemoAvatar" : null;
                }
            }
            return preferences;
        }
        return Db.Database.SqlQuery<AccountSettings>($"""
            SELECT user_id AS "UserId", verified_email AS "VerifiedEmail", verified_at AS "VerifiedAt",
              notifications_enabled AS "NotificationsEnabled", avatar_url AS "AvatarUrl" FROM public.account_preferences WHERE user_id={id}
            """).SingleOrDefault() ?? new AccountSettings { UserId = id };
    }
    public void Verify(AppUser user)
    {
        if (!store.IsPersistent) { var p = Get(user.Id); p.VerifiedEmail = user.Email; p.VerifiedAt = DateTime.UtcNow; return; }
        Db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO public.account_preferences(user_id,verified_email,verified_at) VALUES({user.Id},{user.Email},now())
            ON CONFLICT(user_id) DO UPDATE SET verified_email=excluded.verified_email,verified_at=excluded.verified_at
            """);
    }
    public void Avatar(Guid id, string? url)
    {
        if (!store.IsPersistent) { Get(id).AvatarUrl = url; return; }
        Db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO public.account_preferences(user_id,avatar_url) VALUES({id},{url})
            ON CONFLICT(user_id) DO UPDATE SET avatar_url=excluded.avatar_url
            """);
    }
    public void Notifications(Guid id, bool enabled)
    {
        if (!store.IsPersistent) { Get(id).NotificationsEnabled = enabled; return; }
        Db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO public.account_preferences(user_id,notifications_enabled) VALUES({id},{enabled})
            ON CONFLICT(user_id) DO UPDATE SET notifications_enabled=excluded.notifications_enabled
            """);
    }
    public void Profile(AppUser user, string first, string last, int age, string gender, string address)
    {
        if (user.Role == "Customer") { first = user.FirstName; last = user.LastName; if (user.Birthday.HasValue) age = user.Age; }
        var error = user.Role == "Customer" ? ValidateCustomerProfile(age, gender, address) : RegistrationRules.Validate(first, last, user.Email, age, gender, address, "ProfileValidation123");
        if (error != null) throw new InvalidOperationException(error);
        first = (first ?? "").Trim(); last = (last ?? "").Trim(); address = address.Trim();
        if (store.IsPersistent)
        {
            if (user.Role is "Admin" or "SuperAdmin")
                Db.Database.ExecuteSqlInterpolated($"UPDATE public.admin_users SET first_name={first},last_name={last},age_at_registration={age},gender={gender},address={address} WHERE app_id={user.Id}");
            else
                Db.Database.ExecuteSqlInterpolated($"UPDATE public.customers SET age_at_registration={age},gender={gender},address={address} WHERE app_id={user.Id}");
        }
        if (user.Role != "Customer") { user.FirstName = first; user.LastName = last; user.Name = first + " " + last; }
        user.Age = age; user.Gender = gender; user.Address = address;
    }

    private static string? ValidateCustomerProfile(int age, string gender, string address)
    {
        if (age < RegistrationRules.MinimumAge || age > RegistrationRules.MaximumAge) return $"Enter a valid age ({RegistrationRules.MinimumAge}–{RegistrationRules.MaximumAge}).";
        if (!string.IsNullOrEmpty(gender) && !RegistrationRules.Genders.Contains(gender)) return "Choose a valid option for gender.";
        if (string.IsNullOrWhiteSpace(address) || address.Trim().Length is < 8 or > 500) return "Enter your address (8–500 characters).";
        return GlamByMariya.Core.BusinessRules.ProfanityFilter.ValidateAddress(address);
    }

}
