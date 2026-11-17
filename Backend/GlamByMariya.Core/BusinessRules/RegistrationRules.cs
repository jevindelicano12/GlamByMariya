using System.Text.RegularExpressions;

namespace GlamByMariya.Core.BusinessRules;

// Single source of truth for account registration rules.
// Mirror these rules in the registration form's zod schema (Views/Account/Login.cshtml).
public static partial class RegistrationRules
{
    public static readonly string[] Genders = ["Male", "Female", "Others"];
    public const int MinimumAge = 13;
    public const int MaximumAge = 120;

    [GeneratedRegex(@"^[\p{L} '\-]+$")]
    private static partial Regex NameCharacters();

    // Returns an error message, or null when the submission is valid.
    public static string? Validate(string firstName, string lastName, string email, int age, string gender, string address, string password, bool enforceLegacyAge = true)
    {
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length > 50 || !NameCharacters().IsMatch(firstName.Trim()))
            return "Enter your first name (letters only, up to 50 characters).";
        if (string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length > 50 || !NameCharacters().IsMatch(lastName.Trim()))
            return "Enter your last name (letters only, up to 50 characters).";
        if (string.IsNullOrWhiteSpace(email) || email.Trim().Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email.Trim(), out _))
            return "Enter a valid email address.";
        if (enforceLegacyAge && (age < MinimumAge || age > MaximumAge))
            return $"You must be {MinimumAge}–{MaximumAge} years old to create an account.";
        if (!string.IsNullOrEmpty(gender) && !Genders.Contains(gender))
            return "Choose a valid option for gender.";
        if (string.IsNullOrWhiteSpace(address) || address.Trim().Length < 8 || address.Trim().Length > 500)
            return "Enter your address (8–500 characters).";
        return PasswordPolicy.ValidateNewPassword(password);
    }
}
