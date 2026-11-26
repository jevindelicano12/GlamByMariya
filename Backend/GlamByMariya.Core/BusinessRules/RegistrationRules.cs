using System.Text.RegularExpressions;

namespace GlamByMariya.Core.BusinessRules;

// Single source of truth for account registration rules.
// Mirror these rules in the registration form's zod schema (Views/Account/Login.cshtml).
public static partial class RegistrationRules
{
    public static readonly string[] Genders = ["Male", "Female", "Others"];
    public const int MinimumAge = 13;
    public const int MaximumAge = 110;

    public static int CalculateAge(DateOnly birthday, DateOnly today)
    {
        var age = today.Year - birthday.Year;
        if (birthday.AddYears(age) > today) age--;
        return age;
    }

    public static string? ValidateBirthday(DateOnly? birthday, DateOnly today)
    {
        if (!birthday.HasValue || birthday.Value > today || birthday.Value < new DateOnly(1900, 1, 1))
            return "Enter a valid birthday that is not in the future.";
        var age = CalculateAge(birthday.Value, today);
        if (age < MinimumAge || age > MaximumAge)
            return $"Enter a valid birthday (you must be {MinimumAge}–{MaximumAge} years old).";
        return null;
    }

    [GeneratedRegex(@"^[\p{L} '\-]+$")]
    private static partial Regex NameCharacters();

    // Returns an error message, or null when the submission is valid.
    public static string? Validate(string firstName, string lastName, string email, int age, string gender, string address, string password, bool enforceLegacyAge = true, bool enforceAddress = true) =>
        ValidateProfile(firstName, lastName, email, age, gender, address, enforceLegacyAge, enforceAddress) ?? PasswordPolicy.ValidateNewPassword(password);

    // Profile fields only, for accounts created from an already-hashed password.
    public static string? ValidateProfile(string firstName, string lastName, string email, int age, string gender, string address, bool enforceLegacyAge = true, bool enforceAddress = true)
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
        if ((enforceAddress || !string.IsNullOrWhiteSpace(address)) && (string.IsNullOrWhiteSpace(address) || address.Trim().Length < 8 || address.Trim().Length > 500))
            return "Enter your address (8–500 characters).";
        return null;
    }
}
