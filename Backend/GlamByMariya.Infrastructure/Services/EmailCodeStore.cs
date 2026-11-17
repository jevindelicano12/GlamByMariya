using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace GlamByMariya.Infrastructure.Services;

public enum EmailCodeCheck { Missing, Expired, Locked, Wrong, Ok }

// Storage for one-time email codes. Each (purpose, email) pair has its own code,
// expiry and wrong-attempt counter. Codes are stored only as hashes.
public interface IEmailCodeStore
{
    // Replaces any earlier code for the same purpose and email (attempts reset to 0).
    void Save(string purpose, string email, string codeHash, DateTime expiresAtUtc, string? payload);
    // Atomically checks a code. A correct code is consumed (single use) and its payload returned.
    // A wrong code adds one attempt; once maxAttempts wrong attempts exist, the code is Locked.
    (EmailCodeCheck Result, string? Payload) Consume(string purpose, string email, string codeHash, int maxAttempts, DateTime nowUtc);
    // Payload of a live (unexpired) entry, or null. Does not consume it.
    string? Peek(string purpose, string email, DateTime nowUtc);
    void Remove(string purpose, string email);
    // Returns false while an earlier cooldown for this scope and email is still running.
    bool TryStartCooldown(string scope, string email, TimeSpan length, DateTime nowUtc);
    // Used when sending failed, so the customer can retry immediately.
    void ClearCooldown(string scope, string email);
}

internal static class EmailCodeKeys
{
    internal static string Normalize(string email) => (email ?? "").Trim().ToLowerInvariant();
}

// Demo and local development: process memory only, lost on restart.
public sealed class MemoryEmailCodeStore(IMemoryCache cache) : IEmailCodeStore
{
    private sealed record Entry(string CodeHash, DateTime ExpiresAtUtc, int Attempts, string? Payload);
    private static readonly object Gate = new();
    private static string Key(string purpose, string email) => $"emailcode-store:{purpose}:{EmailCodeKeys.Normalize(email)}";

    public void Save(string purpose, string email, string codeHash, DateTime expiresAtUtc, string? payload)
    {
        lock (Gate) cache.Set(Key(purpose, email), new Entry(codeHash, expiresAtUtc, 0, payload), new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc)));
    }

    public (EmailCodeCheck Result, string? Payload) Consume(string purpose, string email, string codeHash, int maxAttempts, DateTime nowUtc)
    {
        lock (Gate)
        {
            var key = Key(purpose, email);
            if (!cache.TryGetValue<Entry>(key, out var entry) || entry == null) return (EmailCodeCheck.Missing, null);
            if (entry.ExpiresAtUtc <= nowUtc) { cache.Remove(key); return (EmailCodeCheck.Expired, null); }
            if (entry.Attempts >= maxAttempts) return (EmailCodeCheck.Locked, null);
            if (!EmailCodeHash.Equal(entry.CodeHash, codeHash))
            {
                cache.Set(key, entry with { Attempts = entry.Attempts + 1 }, new DateTimeOffset(DateTime.SpecifyKind(entry.ExpiresAtUtc, DateTimeKind.Utc)));
                return (EmailCodeCheck.Wrong, null);
            }
            cache.Remove(key);
            return (EmailCodeCheck.Ok, entry.Payload);
        }
    }

    public string? Peek(string purpose, string email, DateTime nowUtc)
    {
        lock (Gate)
            return cache.TryGetValue<Entry>(Key(purpose, email), out var entry) && entry != null && entry.ExpiresAtUtc > nowUtc ? entry.Payload ?? "" : null;
    }

    public void Remove(string purpose, string email)
    {
        lock (Gate) cache.Remove(Key(purpose, email));
    }

    public bool TryStartCooldown(string scope, string email, TimeSpan length, DateTime nowUtc)
    {
        lock (Gate)
        {
            var key = $"emailcode-cooldown:{scope}:{EmailCodeKeys.Normalize(email)}";
            if (cache.TryGetValue(key, out _)) return false;
            cache.Set(key, true, length);
            return true;
        }
    }

    public void ClearCooldown(string scope, string email)
    {
        lock (Gate) cache.Remove($"emailcode-cooldown:{scope}:{EmailCodeKeys.Normalize(email)}");
    }
}

// Supabase PostgreSQL: survives restarts and works across instances.
// Requires public.email_codes and public.email_code_cooldowns (database/v2/migrations/0005_email_codes.sql).
public sealed class PostgresEmailCodeStore(NpgsqlDataSource source) : IEmailCodeStore
{
    public void Save(string purpose, string email, string codeHash, DateTime expiresAtUtc, string? payload)
    {
        using var command = source.CreateCommand("""
            INSERT INTO public.email_codes(purpose, email, code_hash, expires_at, attempts, payload)
            VALUES (@purpose, @email, @hash, @expires, 0, @payload)
            ON CONFLICT (purpose, email) DO UPDATE
            SET code_hash = EXCLUDED.code_hash, expires_at = EXCLUDED.expires_at, attempts = 0, payload = EXCLUDED.payload, created_at = now()
            """);
        Bind(command, purpose, email);
        command.Parameters.AddWithValue("hash", codeHash);
        command.Parameters.AddWithValue("expires", DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc));
        command.Parameters.AddWithValue("payload", (object?)payload ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public (EmailCodeCheck Result, string? Payload) Consume(string purpose, string email, string codeHash, int maxAttempts, DateTime nowUtc)
    {
        using var connection = source.OpenConnection();
        using var transaction = connection.BeginTransaction();
        string storedHash; DateTime expires; int attempts; string? payload;
        using (var read = new NpgsqlCommand("""
            SELECT code_hash, expires_at, attempts, payload FROM public.email_codes
            WHERE purpose = @purpose AND email = @email FOR UPDATE
            """, connection, transaction))
        {
            Bind(read, purpose, email);
            using var reader = read.ExecuteReader();
            if (!reader.Read()) { reader.Close(); transaction.Commit(); return (EmailCodeCheck.Missing, null); }
            storedHash = reader.GetString(0); expires = reader.GetDateTime(1); attempts = reader.GetInt32(2);
            payload = reader.IsDBNull(3) ? null : reader.GetString(3);
        }
        EmailCodeCheck result;
        if (expires <= DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)) { Delete(connection, transaction, purpose, email); result = EmailCodeCheck.Expired; payload = null; }
        else if (attempts >= maxAttempts) { result = EmailCodeCheck.Locked; payload = null; }
        else if (!EmailCodeHash.Equal(storedHash, codeHash))
        {
            using var bump = new NpgsqlCommand("UPDATE public.email_codes SET attempts = attempts + 1 WHERE purpose = @purpose AND email = @email", connection, transaction);
            Bind(bump, purpose, email); bump.ExecuteNonQuery();
            result = EmailCodeCheck.Wrong; payload = null;
        }
        else { Delete(connection, transaction, purpose, email); result = EmailCodeCheck.Ok; }
        transaction.Commit();
        return (result, payload);
    }

    public string? Peek(string purpose, string email, DateTime nowUtc)
    {
        using var command = source.CreateCommand("SELECT coalesce(payload, '') FROM public.email_codes WHERE purpose = @purpose AND email = @email AND expires_at > @now");
        Bind(command, purpose, email);
        command.Parameters.AddWithValue("now", DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        return command.ExecuteScalar() as string;
    }

    public void Remove(string purpose, string email)
    {
        using var command = source.CreateCommand("DELETE FROM public.email_codes WHERE purpose = @purpose AND email = @email");
        Bind(command, purpose, email);
        command.ExecuteNonQuery();
    }

    public bool TryStartCooldown(string scope, string email, TimeSpan length, DateTime nowUtc)
    {
        // One statement: insert, or take over an expired row; a running cooldown returns no row.
        using var command = source.CreateCommand("""
            INSERT INTO public.email_code_cooldowns(scope, email, until_at) VALUES (@scope, @email, @until)
            ON CONFLICT (scope, email) DO UPDATE SET until_at = EXCLUDED.until_at
            WHERE public.email_code_cooldowns.until_at <= @now
            RETURNING 1
            """);
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("email", EmailCodeKeys.Normalize(email));
        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        command.Parameters.AddWithValue("until", now.Add(length));
        command.Parameters.AddWithValue("now", now);
        return command.ExecuteScalar() != null;
    }

    public void ClearCooldown(string scope, string email)
    {
        using var command = source.CreateCommand("DELETE FROM public.email_code_cooldowns WHERE scope = @scope AND email = @email");
        command.Parameters.AddWithValue("scope", scope);
        command.Parameters.AddWithValue("email", EmailCodeKeys.Normalize(email));
        command.ExecuteNonQuery();
    }

    private static void Delete(NpgsqlConnection connection, NpgsqlTransaction transaction, string purpose, string email)
    {
        using var delete = new NpgsqlCommand("DELETE FROM public.email_codes WHERE purpose = @purpose AND email = @email", connection, transaction);
        Bind(delete, purpose, email); delete.ExecuteNonQuery();
    }

    private static void Bind(NpgsqlCommand command, string purpose, string email)
    {
        command.Parameters.AddWithValue("purpose", purpose);
        command.Parameters.AddWithValue("email", EmailCodeKeys.Normalize(email));
    }
}

internal static class EmailCodeHash
{
    internal static bool Equal(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left ?? ""), System.Text.Encoding.UTF8.GetBytes(right ?? ""));
}
