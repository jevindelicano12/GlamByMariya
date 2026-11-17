namespace GlamByMariya.Infrastructure.Services;

public static class AuthenticationPasswordCoordinator
{
    // Cross-system operations cannot be atomic. Link first so a successful
    // Supabase password change can never leave an old local hash usable.
    public static async Task<bool> UpdateSupabasePasswordAsync(
        Action ensureIdentityLinked, Func<Task> updateSupabase, Action syncLocal)
    {
        ensureIdentityLinked();
        try { await updateSupabase(); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or TimeoutException or PasswordUpdateUncertainException) { return false; }
        try { syncLocal(); return true; }
        catch (Exception) { return false; }
    }
}

public sealed class PasswordUpdateUncertainException : Exception;
