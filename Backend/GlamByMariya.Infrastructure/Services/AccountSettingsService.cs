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
        if (age < 0 || age > 9999) return "Enter a valid age.";
        if (!string.IsNullOrEmpty(gender) && !RegistrationRules.Genders.Contains(gender)) return "Choose a valid option for gender.";
        return string.IsNullOrWhiteSpace(address) || address.Trim().Length is < 8 or > 500 ? "Enter your address (8–500 characters)." : null;
    }

    // The subject and epoch must come from the original validated session.
    public async Task<bool> ProfileWithDurableEpoch(AppUser user, AccountSessionEvidence evidence, Guid verifiedSubject,
        string first, string last, int age, string gender, string address, CancellationToken ct = default)
    {
        if (user.Id == Guid.Empty || verifiedSubject == Guid.Empty || evidence.AccountId != user.Id || evidence.Role != user.Role ||
            user.Role is not ("Customer" or "Admin" or "SuperAdmin") ||
            !long.TryParse(evidence.CredentialEpoch, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch) || epoch < 1 ||
            string.IsNullOrEmpty(evidence.AccountVersion)) return false;
        if (user.Role == "Customer") { first = user.FirstName; last = user.LastName; if (user.Birthday.HasValue) age = user.Age; }
        var error = user.Role == "Customer" ? ValidateCustomerProfile(age, gender, address) : RegistrationRules.Validate(first, last, user.Email, age, gender, address, "ProfileValidation123");
        if (error != null) throw new InvalidOperationException(error);
        first = first.Trim(); last = last.Trim(); address = address.Trim();

        await using var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // Same identity-before-owner lock order as the durable credential store.
        await using var identity = new NpgsqlCommand("""
            SELECT customer_id,admin_id,login FROM public.account_identities
            WHERE id=@id AND credential_epoch=@epoch AND credential_sync_state='Ready'
              AND auth_state='Supabase' AND supabase_user_id=@subject FOR UPDATE
            """, connection, tx);
        identity.Parameters.AddWithValue("id", user.Id); identity.Parameters.AddWithValue("epoch", epoch); identity.Parameters.AddWithValue("subject", verifiedSubject);
        long? customer; long? admin; string login;
        await using (var reader = await identity.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct) || reader.IsDBNull(2)) return false;
            customer = reader.IsDBNull(0) ? null : reader.GetInt64(0);
            admin = reader.IsDBNull(1) ? null : reader.GetInt64(1); login = reader.GetString(2);
        }
        if (string.IsNullOrWhiteSpace(login) || !string.Equals(login.Trim(), user.Email.Trim(), StringComparison.OrdinalIgnoreCase) ||
            (user.Role == "Customer" ? !customer.HasValue || admin.HasValue : !admin.HasValue || customer.HasValue)) return false;
        var table = user.Role == "Customer" ? "customers" : "admin_users";
        var loginField = user.Role == "Customer" ? "email" : "coalesce(nullif(btrim(email),''),username)";
        var roleCheck = user.Role == "Customer" ? "TRUE" : "role=@role";
        await using (var owner = new NpgsqlCommand($"SELECT password_hash FROM public.{table} WHERE id=@owner AND app_id=@id AND is_active AND (locked_until IS NULL OR locked_until<=statement_timestamp()) AND {roleCheck} AND lower(btrim({loginField}))=lower(btrim(@login)) FOR UPDATE", connection, tx))
        {
            owner.Parameters.AddWithValue("owner", customer ?? admin!.Value); owner.Parameters.AddWithValue("id", user.Id);
            owner.Parameters.AddWithValue("login", login); if (user.Role != "Customer") owner.Parameters.AddWithValue("role", user.Role);
            if (await owner.ExecuteScalarAsync(ct) is not string hash || string.IsNullOrEmpty(hash) ||
                !string.Equals(AccountSession.Version(hash), evidence.AccountVersion, StringComparison.Ordinal)) return false;
        }
        await using var integrity = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public.customers WHERE app_id=@id)+(SELECT count(*) FROM public.admin_users WHERE app_id=@id)=1
              AND (SELECT count(*) FROM public.account_identities WHERE lower(btrim(login))=lower(btrim(@login)))=1
              AND (SELECT count(*) FROM public.account_identities WHERE supabase_user_id=@subject)=1
              AND ((SELECT count(*) FROM public.customers WHERE lower(btrim(email))=lower(btrim(@login)))+
                   (SELECT count(*) FROM public.admin_users WHERE lower(btrim(coalesce(nullif(btrim(email),''),username)))=lower(btrim(@login))))=1
            """, connection, tx);
        integrity.Parameters.AddWithValue("id", user.Id); integrity.Parameters.AddWithValue("login", login); integrity.Parameters.AddWithValue("subject", verifiedSubject);
        if ((bool?)await integrity.ExecuteScalarAsync(ct) != true) return false;
        var nameUpdate = user.Role == "Customer" ? "" : "first_name=@first,last_name=@last,";
        await using var update = new NpgsqlCommand($"UPDATE public.{table} SET {nameUpdate}age_at_registration=@age,gender=@gender,address=@address WHERE app_id=@id", connection, tx);
        update.Parameters.AddWithValue("first", first); update.Parameters.AddWithValue("last", last); update.Parameters.AddWithValue("age", age);
        update.Parameters.AddWithValue("gender", gender); update.Parameters.AddWithValue("address", address); update.Parameters.AddWithValue("id", user.Id);
        if (await update.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Profile update could not be confirmed.");
        await tx.CommitAsync(ct);
        if (user.Role != "Customer") { user.FirstName = first; user.LastName = last; user.Name = first + " " + last; }
        user.Age = age; user.Gender = gender; user.Address = address;
        return true;
    }
}
