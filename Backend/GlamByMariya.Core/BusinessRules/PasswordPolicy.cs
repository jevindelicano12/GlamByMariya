namespace GlamByMariya.Core.BusinessRules;

/// <summary>
/// Standalone policy for newly chosen passwords, not existing-password login.
/// Not connected to any authentication flow. The maximum is PROVISIONAL:
/// Supabase's effective character/UTF-8 byte limits remain unverified.
/// Success is local policy acceptance, not proof of provider compatibility.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const int ProvisionalMaximumLength = 128;

    /// <summary>
    /// Returns a fixed error, or null on acceptance. Length uses UTF-16 code
    /// units (string.Length), not Unicode scalars, graphemes or UTF-8 bytes.
    /// ASCII letters/digits satisfy composition; other characters are allowed.
    /// Does not transform, retain, hash, log, persist or transmit the input.
    /// </summary>
    public static string? ValidateNewPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return "Enter a new password.";
        if (password.Length < MinimumLength || password.Length > ProvisionalMaximumLength)
            return "Use 8–128 characters for the new password. Some characters, such as emoji, count as two.";

        var hasLetter = false;
        var hasDigit = false;
        foreach (var character in password)
        {
            hasLetter |= character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            hasDigit |= character is >= '0' and <= '9';
        }
        return hasLetter && hasDigit ? null : "Include at least one English letter (A–Z) and one number (0–9).";
    }

    /// <summary>
    /// Separate required-confirmation check. Call ValidateNewPassword as well:
    /// equality alone does not establish password-policy acceptance. No trimming,
    /// normalization, case folding or truncation is performed on either input.
    /// </summary>
    public static string? ValidateConfirmation(string? password, string? confirmation)
    {
        if (string.IsNullOrEmpty(password)) return "Enter a new password before confirming it.";
        if (string.IsNullOrEmpty(confirmation)) return "Confirm the new password.";
        return string.Equals(password, confirmation, StringComparison.Ordinal)
            ? null : "New passwords do not match.";
    }
}
