using System.Text;
using GlamByMariya.Core.BusinessRules;

// All inputs are synthetic and never printed, including on failure. Actual pure
// application methods only: no source-contract mirrors, host, network, database,
// filesystem access, logging providers or persistent test data.
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL (actual validator): " + label);
    passed++;
    Console.WriteLine("PASS (actual validator): " + label);
}

const string required = "Enter a new password.";
const string length = "Use 8–128 characters for the new password. Some characters, such as emoji, count as two.";
const string composition = "Include at least one English letter (A–Z) and one number (0–9).";
var cases = new (string Label, string? Input, string? Error)[]
{
    ("null input", null, required),
    ("empty input", "", required),
    ("seven units", "Abc1234", length),
    ("eight units", "Abcd1234", null),
    ("letters only", new string('a', 8), composition),
    ("digits only", new string('1', 8), composition),
    ("symbols only", "!@#$%^&*", composition),
    ("spaces only", new string(' ', 8), composition),
    ("whitespace only", "\t\t\t\t\t\t\t\t", composition),
    ("Unicode spaces only", new string('\u2003', 8), composition),
    ("mixed symbols", "A1!@#$%^", null),
    ("leading/trailing spaces allowed unchanged", " A1 bc d ", null),
    ("uppercase ASCII satisfies letter", "A1234567", null),
    ("lowercase ASCII satisfies letter", "a1234567", null),
    ("127 units", new string('a', 126) + "1", null),
    ("128 units", new string('a', 127) + "1", null),
    ("129 units", new string('a', 128) + "1", length),
    ("large input rejected by length", new string('a', 4096) + "1", length),
    ("non-ASCII letters do not satisfy ASCII requirement", new string('\u00e9', 7) + "1", composition),
    ("non-ASCII digits do not satisfy ASCII requirement", "Abcdefg\u0661", composition),
    ("fullwidth composition is not ASCII", "\uff21\uff22\uff23\uff24\uff11\uff12\uff13\uff14", composition),
    ("Unicode additions allowed", "A1\u00e9\u0661\u4e2dxyz", null),
    ("combining character addition allowed", "A1e\u0301abcd", null),
    ("supplementary letter alone cannot satisfy ASCII", "\U00010400\U00010400\U0001040011", composition),
    ("supplementary addition at eight units", "\U0001f600A1abcd", null),
    ("supplementary addition at seven units", "\U0001f600A1abc", length),
    ("supplementary addition at 127 units", "A1" + new string('x', 123) + "\U0001f600", null),
    ("supplementary addition at 128 units", "A1" + new string('x', 124) + "\U0001f600", null),
    ("supplementary addition at 129 units", "A1" + new string('x', 125) + "\U0001f600", length),
    ("128 multibyte units accepted locally", new string('\u00e9', 126) + "A1", null)
};
foreach (var sample in cases)
{
    var before = sample.Input?.ToCharArray();
    var result = PasswordPolicy.ValidateNewPassword(sample.Input);
    Check(result == sample.Error, sample.Label + " fixed result");
    Check(PasswordPolicy.ValidateNewPassword(sample.Input) == result, sample.Label + " deterministic result");
    Check(before == null ? sample.Input == null : sample.Input != null && before.SequenceEqual(sample.Input),
        sample.Label + " input unchanged");
}

const string passwordRequired = "Enter a new password before confirming it.";
const string confirmationRequired = "Confirm the new password.";
const string mismatch = "New passwords do not match.";
var confirmations = new (string Label, string? Password, string? Confirmation, string? Error)[]
{
    ("exact match", "Abcd1234", "Abcd1234", null),
    ("missing password", null, "Abcd1234", passwordRequired),
    ("both missing", null, null, passwordRequired),
    ("both empty", "", "", passwordRequired),
    ("missing confirmation", "Abcd1234", null, confirmationRequired),
    ("empty confirmation", "Abcd1234", "", confirmationRequired),
    ("case mismatch", "Abcd1234", "abcd1234", mismatch),
    ("leading space mismatch", "Abcd1234", " Abcd1234", mismatch),
    ("trailing space mismatch", "Abcd1234", "Abcd1234 ", mismatch),
    ("matching spaces retained", " Abcd1234 ", " Abcd1234 ", null),
    ("normalization difference rejected", "A1\u00e9bcdef", "A1e\u0301bcdef", mismatch),
    ("supplementary exact match", "\U0001f600A1abcd", "\U0001f600A1abcd", null),
    ("difference beyond maximum not truncated", new string('a', 128) + "1", new string('a', 128) + "2", mismatch)
};
foreach (var sample in confirmations)
{
    var originalPassword = sample.Password?.ToCharArray();
    var originalConfirmation = sample.Confirmation?.ToCharArray();
    Check(PasswordPolicy.ValidateConfirmation(sample.Password, sample.Confirmation) == sample.Error,
        sample.Label + " ordinal confirmation");
    Check((originalPassword == null ? sample.Password == null : sample.Password != null && originalPassword.SequenceEqual(sample.Password))
        && (originalConfirmation == null ? sample.Confirmation == null : sample.Confirmation != null && originalConfirmation.SequenceEqual(sample.Confirmation)),
        sample.Label + " both inputs unchanged");
}

Check(PasswordPolicy.MinimumLength == 8 && PasswordPolicy.ProvisionalMaximumLength == 128,
    "documented provisional UTF-16 boundaries");
Check("\U0001f600A1abcd".Length == 8 && Encoding.UTF8.GetByteCount("\U0001f600A1abcd") == 10,
    "UTF-16 boundary differs from UTF-8 byte count");
var multibyte = new string('\u00e9', 126) + "A1";
Check(multibyte.Length == 128 && Encoding.UTF8.GetByteCount(multibyte) == 254
    && PasswordPolicy.ValidateNewPassword(multibyte) == null,
    "no unverified provider byte restriction invented");
Check(PasswordPolicy.ValidateConfirmation("short", "short") == null
    && PasswordPolicy.ValidateNewPassword("short") != null,
    "confirmation is separate and never substitutes for policy validation");

// Registration now delegates to the shared policy. Exercise both actual pure methods.
bool ExistingRegistrationAccepts(string value) => RegistrationRules.Validate(
    "Test", "Member", "member@example.test", 25, "", "Synthetic test address", value) == null;
var unicodeDigit = "Abcdefg\u0661";
Check(!ExistingRegistrationAccepts(unicodeDigit) && PasswordPolicy.ValidateNewPassword(unicodeDigit) != null,
    "registration and shared policy both require an ASCII digit");
var overMaximum = new string('a', 128) + "1";
Check(!ExistingRegistrationAccepts(overMaximum) && PasswordPolicy.ValidateNewPassword(overMaximum) != null,
    "registration and shared policy both enforce the provisional maximum");
var unicodeAndSymbols = "A1é中!xyz";
Check(ExistingRegistrationAccepts(unicodeAndSymbols) && PasswordPolicy.ValidateNewPassword(unicodeAndSymbols) == null,
    "registration accepts Unicode and symbols alongside ASCII composition");
Check(ExistingRegistrationAccepts("Abcd1234") && PasswordPolicy.ValidateNewPassword("Abcd1234") == null,
    "common existing ASCII input remains accepted by both validators");

Console.WriteLine($"Actual standalone validator checks: {passed} passed; 0 failed; 0 skipped.");
Console.WriteLine("No controller, browser, OTP, provider, persistent repository, cookie or production integration tested here.");
