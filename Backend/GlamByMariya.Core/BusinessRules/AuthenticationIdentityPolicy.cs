namespace GlamByMariya.Core.BusinessRules;

public static class AuthenticationIdentityPolicy
{
    public const string Unknown = "Unknown";
    public const string LegacyOnly = "LegacyOnly";
    public const string Supabase = "Supabase";

    // Supabase-linked identities never fall back to a local hash. Before the
    // restriction rollout, Unknown remains transitional; after activation only
    // explicitly reviewed LegacyOnly accounts may use fallback.
    public static bool CanUseLegacyFallback(string? state, bool restrictUnknown = true) =>
        state == LegacyOnly || (!restrictUnknown && state == Unknown);

    public static bool CanUseLegacyFallback(string? state, Guid? supabaseUserId, bool restrictUnknown) =>
        !supabaseUserId.HasValue && CanUseLegacyFallback(state, restrictUnknown);

    public static void ValidateLink(Guid? existingSupabaseUserId, string? existingState, Guid supabaseUserId)
    {
        if (supabaseUserId == Guid.Empty) throw new InvalidOperationException("The Supabase identity is invalid.");
        if (existingSupabaseUserId.HasValue && existingSupabaseUserId.Value != supabaseUserId)
            throw new InvalidOperationException("This account is already linked to a different Supabase identity.");
        if (existingState == Supabase && existingSupabaseUserId != supabaseUserId)
            throw new InvalidOperationException("The account identity does not match Supabase.");
    }
}
