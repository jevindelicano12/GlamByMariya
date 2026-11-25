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
            return "Password must be 8–128 characters.";

        var hasLower = false;
        var hasUpper = false;
        var hasDigit = false;
        var hasSpecial = false;
        foreach (var character in password)
        {
            if (character < 0x20 || character > 0x7E)
                return "Emojis and unsupported characters are not allowed in passwords.";
            if (character is >= 'a' and <= 'z') hasLower = true;
            else if (character is >= 'A' and <= 'Z') hasUpper = true;
            else if (character is >= '0' and <= '9') hasDigit = true;
            else hasSpecial = true;
        }
        return hasLower && hasUpper && hasDigit && hasSpecial
            ? null
            : "Password must include at least 1 uppercase letter, 1 lowercase letter, 1 number, and 1 special character.";
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
