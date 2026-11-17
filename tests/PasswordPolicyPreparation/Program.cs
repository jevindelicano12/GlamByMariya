using System.Text;
using GlamByMariya.Core.BusinessRules;

// No host, HTTP, database, credentials, configuration or provider dependencies.
// RegistrationRules and PasswordPolicy are actual production pure behavior;
// controller ordering and browser predicates remain mirrors/source contracts.
var passed = 0;
var skipped = 0;

void Check(bool condition, string category, string label)
{
    if (!condition) throw new InvalidOperationException("FAIL [" + category + "]: " + label);
    passed++;
    Console.WriteLine("PASS [" + category + "]: " + label);
}
void Skip(string label)
{
    skipped++;
    Console.WriteLine("SKIP [proposed/unimplemented]: " + label);
}

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (root != null && !File.Exists(Path.Combine(root.FullName, "MakeupSystem.slnx"))) root = root.Parent;
if (root == null) throw new InvalidOperationException("Repository source root unavailable.");
string Read(string relative) => File.ReadAllText(Path.Combine(root.FullName, relative));
var controller = Read("Frontend/GlamByMariya.Web/Controllers/AccountController.cs");
var registration = Read("Backend/GlamByMariya.Core/BusinessRules/RegistrationRules.cs");
var loginView = Read("Frontend/GlamByMariya.Web/Views/Account/Login.cshtml");
var resetView = Read("Frontend/GlamByMariya.Web/Views/Account/ResetPassword.cshtml");
var settingsView = Read("Frontend/GlamByMariya.Web/Views/Account/Settings.cshtml");
var inviteView = Read("Frontend/GlamByMariya.Web/Views/Account/AcceptInvite.cshtml");
var postgresStore = Read("Backend/GlamByMariya.Infrastructure/Data/PostgresStudioStore.cs");
var publicStore = Read("Backend/GlamByMariya.Infrastructure/Data/PublicStudioStore.cs");

string Action(string start, string end)
{
    var first = controller.IndexOf(start, StringComparison.Ordinal);
    var last = first < 0 ? -1 : controller.IndexOf(end, first + start.Length, StringComparison.Ordinal);
    if (first < 0 || last <= first) throw new InvalidOperationException("Expected action source boundaries unavailable.");
    return controller[first..last];
}
var change = Action("public async Task<IActionResult> ChangePassword(", "public IActionResult Login(");
var reset = Action("public async Task<IActionResult> ResetPassword(string email, string code, string newPassword,", "public IActionResult AcceptInvite(");
var register = Action("public async Task<IActionResult> Register(", "public IActionResult Verify(");
var invite = Action("public async Task<IActionResult> AcceptInvite(", "private AppUser? FindLocal(");

bool Before(string source, string first, params string[] later)
{
    var index = source.IndexOf(first, StringComparison.Ordinal);
    return index >= 0 && later.All(token => source.IndexOf(token, StringComparison.Ordinal) > index);
}

// ChangePassword now uses the actual shared helper. Ordering assertions remain
// source contracts; its behavior is exercised by PasswordChangePolicyChecks.
Check(change.Contains("PasswordPolicy.ValidateNewPassword(newPassword)", StringComparison.Ordinal)
    && change.Contains("PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword)", StringComparison.Ordinal),
    "source contract", "authenticated password change delegates to shared policy and confirmation");
Check(reset.Contains("PasswordPolicy.ValidateNewPassword(newPassword)", StringComparison.Ordinal)
    && reset.Contains("PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword)", StringComparison.Ordinal),
    "source contract", "reset action uses the shared password and confirmation validators");
Check(registration.Contains("PasswordPolicy.ValidateNewPassword(password)", StringComparison.Ordinal)
    && !registration.Contains("HasLetter", StringComparison.Ordinal)
    && !registration.Contains("HasDigit", StringComparison.Ordinal),
    "source contract", "registration delegates only its password rule to the shared validator");
Check(loginView.Contains(".regex(/[A-Za-z]/", StringComparison.Ordinal)
    && loginView.Contains(".regex(/[0-9]/", StringComparison.Ordinal)
    && loginView.Contains(".max(128", StringComparison.Ordinal),
    "source contract", "registration browser schema matches ASCII composition and provisional maximum");
Check(Before(register, "RegistrationRules.Validate(", "FindLocal(email)", "supabase.SignUpAsync(", "codes.HoldRegistration(email, pending)"),
    "source contract", "registration validates before duplicate lookup, provider signup and pending storage");
Check(Before(register, "RegistrationRules.Validate(", "FindLocal(email)", "codes.RequestRegistrationCodeAsync("),
    "source contract", "registration validates before duplicate lookup and local verification-code issuance");
Check(Before(invite, "RegistrationRules.Validate(", "FindLocal(email)", "invites.Accept(", "store.CreateElevatedUser(", "supabase.CreateConfirmedUserAsync(", "SignIn(user)"),
    "source contract", "invitation validates before duplicate lookup, token consumption, creation, provisioning and sign-in");
Check(Before(change, "PasswordPolicy.ValidateNewPassword(newPassword)", "PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword)",
        "supabase.SignInWithPasswordAsync(", "supabase.UpdatePasswordAsync(", "SetLocalPassword(", "SignOutAsync("),
    "source contract", "shared policy and confirmation precede provider/current-password, local mutation, and sign-out paths");
Check(Before(reset, "PasswordPolicy.ValidateNewPassword(newPassword)",
        "PasswordPolicy.ValidateConfirmation(newPassword, confirmPassword)",
        "supabase.VerifyOtpAsync(", "codes.ConfirmPasswordResetCode(", "RequireVerifiedBinding(",
        "supabase.UpdatePasswordAsync(", "SetLocalPassword("),
    "source contract", "both shared validators precede either recovery proof consumption and all mutation");
Check(Before(reset, "if (passwordError != null)", "supabase.VerifyOtpAsync(", "codes.ConfirmPasswordResetCode(")
    && reset.Contains("if (confirmationError != null)", StringComparison.Ordinal),
    "source contract", "reset policy errors return before Supabase and local OTP calls");
Check(resetView.Contains("name=\"newPassword\" type=\"password\" required", StringComparison.Ordinal)
    && resetView.Contains("name=\"confirmPassword\" type=\"password\" required", StringComparison.Ordinal),
    "source contract", "reset password and confirmation fields remain required");
Check(settingsView.Contains("name=\"confirmPassword\"", StringComparison.Ordinal)
    && settingsView.Contains("maxlength=\"128\"", StringComparison.Ordinal),
    "source contract", "change form supplies confirmation and maximum");
Check(resetView.Contains("name=\"newPassword\" type=\"password\" required minlength=\"8\" maxlength=\"128\"", StringComparison.Ordinal)
    && resetView.Contains("name=\"confirmPassword\" type=\"password\" required minlength=\"8\" maxlength=\"128\"", StringComparison.Ordinal)
    && inviteView.Contains("name=\"password\" type=\"password\" required minlength=\"8\" maxlength=\"128\"", StringComparison.Ordinal),
    "source contract", "reset and invitation fields match the shared limits");
Check(loginView.Contains("register ?", StringComparison.Ordinal)
    && loginView.Contains("minlength=", StringComparison.Ordinal)
    && loginView.Contains("maxlength=", StringComparison.Ordinal)
    && loginView.Contains("current-password", StringComparison.Ordinal)
    && loginView.Contains("new-password", StringComparison.Ordinal),
    "source contract", "shared login/register field applies length attributes only for registration");
Check(Before(postgresStore, "var error=RegistrationRules.Validate(firstName,lastName,email,age,gender,address,password)", "HashPassword(user,password)", "Transaction(Actor"),
    "source contract", "legacy AddUser shares registration password checks before hashing and insert");
Check(Before(postgresStore, "public virtual AppUser CreateElevatedUser(", "return AddUser(firstName,lastName,email,password,role,age,gender,address)"),
    "source contract", "legacy elevated creation delegates to shared AddUser validation");
Check(Before(publicStore, "var error = RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password);", "HashPassword(user, password)", "Transaction(ActorId"),
    "source contract", "public AddUser shares registration password checks before hashing and insert");
var elevatedStart = publicStore.IndexOf("public override AppUser CreateElevatedUser(", StringComparison.Ordinal);
var elevatedEnd = publicStore.IndexOf("public override void SaveCatalog(", elevatedStart, StringComparison.Ordinal);
var publicElevated = elevatedStart >= 0 && elevatedEnd > elevatedStart ? publicStore[elevatedStart..elevatedEnd] : "";
Check(Before(publicElevated, "var error = RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password);", "HashPassword(user, password)", "Transaction(ActorId"),
    "source contract", "public elevated creation shares registration password checks before hashing and insert");
Check(Before(postgresStore, "PasswordPolicy.ValidateNewPassword(newPassword)", "FindUser(email)", "HashPassword(tmp, newPassword)", "Transaction(existing.Id"),
    "source contract", "legacy repository validates before lookup, hashing, and transaction");
Check(Before(publicStore, "PasswordPolicy.ValidateNewPassword(newPassword)", "FindUser(email)", "HashPassword(tmp, newPassword)", "Transaction(existing.Id"),
    "source contract", "public repository validates before lookup, hashing, and transaction");
Check(change.Contains("if (!synchronized)", StringComparison.Ordinal)
    && change.Contains("await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)", StringComparison.Ordinal)
    && reset.Contains("if (!synchronized)", StringComparison.Ordinal)
    && reset.Contains("Supabase may have accepted the new password", StringComparison.Ordinal),
    "source contract", "Phase 1.6 result handling remains present");

// Registration, Change and Reset columns document shared-policy acceptance;
// browser registration mirrors the rendered registration-only constraints.
var cases = new (string Label, string? Value, bool Registration, bool Change, bool Reset, bool BrowserRegistration)[]
{
    ("missing", null, false, false, false, false),
    ("empty", "", false, false, false, false),
    ("below minimum", "Abc1234", false, false, false, false),
    ("exactly eight mixed", "Abcd1234", true, true, true, true),
    ("letter only", "abcdefgh", false, false, false, false),
    ("digit only", "12345678", false, false, false, false),
    ("symbols only", "!@#$%^&*", false, false, false, false),
    ("mixed with symbols", "Ab12!@#$", true, true, true, true),
    ("mixed with spaces", " A1 bc d", true, true, true, true),
    ("all spaces", new string(' ', 8), false, false, false, false),
    ("all whitespace", "\t\t\t\t\t\t\t\t", false, false, false, false),
    ("127 UTF-16 units", new string('a', 126) + "1", true, true, true, true),
    ("128 UTF-16 units", new string('a', 127) + "1", true, true, true, true),
    ("129 UTF-16 units", new string('a', 128) + "1", false, false, false, false),
    ("Unicode BMP letters with ASCII digit", "\u00e9\u00e9\u00e9\u00e9\u00e9\u00e9\u00e91", false, false, false, false),
    ("ASCII letter with Unicode decimal digit", "Abcdefg\u0661", false, false, false, false),
    ("Unicode letter and decimal digit", "\u00e9bcdefg\u0661", false, false, false, false),
    ("only non-ASCII letters and decimal digit", "\u00e9\u00e9\u00e9\u00e9\u00e9\u00e9\u00e9\u0661", false, false, false, false),
    ("supplementary letter is surrogate pair", "\U00010400bcdef1", true, true, true, true),
    ("supplementary letter without ASCII letter", "\U00010400\U00010400\U0001040011", false, false, false, false),
    ("emoji with ASCII letter and digit", "\U0001f600A1abcd", true, true, true, true)
};
foreach (var sample in cases)
{
    var actualRegistration = RegistrationRules.Validate("Test", "Member", "member@example.test", 25, "", "Synthetic test address", sample.Value!) == null;
    Check(actualRegistration == sample.Registration, "actual RegistrationRules", sample.Label);
    Check(CurrentChange(sample.Value) == sample.Change, "integrated change-policy mirror ONLY", sample.Label + " change");
    Check(CurrentReset(sample.Value) == sample.Reset, "integrated reset-policy mirror ONLY", sample.Label + " reset");
    Check(CurrentBrowserRegistration(sample.Value) == sample.BrowserRegistration, "registration browser mirror ONLY", sample.Label + " browser registration");
}

var confirmations = new (string Label, string Password, string? Confirmation, bool Matches)[]
{
    ("exact match", "Abcd1234", "Abcd1234", true),
    ("case differs", "Abcd1234", "abcd1234", false),
    ("leading space differs", "Abcd1234", " Abcd1234", false),
    ("trailing space differs", "Abcd1234", "Abcd1234 ", false),
    ("missing confirmation", "Abcd1234", null, false),
    ("empty confirmation", "Abcd1234", "", false),
    ("normalization is not equality", "\u00e9Abcd123", "e\u0301Abcd123", false)
};
foreach (var sample in confirmations)
    Check((sample.Password == sample.Confirmation) == sample.Matches, "current change confirmation mirror ONLY", sample.Label);

Check("\U0001f600A1abcd".Length == 8 && Encoding.UTF8.GetByteCount("\U0001f600A1abcd") == 10,
    "length compatibility", "eight UTF-16 units can exceed eight UTF-8 bytes");
var multibyte = new string('\u00e9', 126) + "A1";
Check(multibyte.Length == 128 && Encoding.UTF8.GetByteCount(multibyte) == 254 && CurrentChange(multibyte),
    "length compatibility", "integrated change-policy mirror accepts 128 units that encode to 254 bytes");
Check(Encoding.UTF8.GetByteCount(new string('a', 127) + "1") == 128,
    "length compatibility", "ASCII units and bytes agree at current boundary");
Check("\U00010400".Length == 2 && !"\U00010400".Any(char.IsLetter),
    "Unicode compatibility", "char-based change validation does not recognize supplementary letters alone");

// Deliberately unconditional: a placeholder class or source token must never
// promote these future requirements into passing enforcement evidence.
Skip("The Zod/browser constraint-validation script is not executed in a browser; MVC rendering is checked separately.");
Skip("Persistent AddUser/CreateElevatedUser runtime tests require a database and are intentionally not run; their validation ordering is source checked.");
Skip("Supabase effective limits, UTF-8 handling and configured policy cannot be verified offline by this suite.");

Console.WriteLine($"Password-policy preparation: {passed} checks passed; 0 failed; {skipped} proposed/enforcement checks intentionally skipped.");
Console.WriteLine("Actual production execution covers pure validators only. Controller/browser mirrors and source contracts are not end-to-end security proof.");

static bool CurrentReset(string? password) => PasswordPolicyMirror(password);
static bool CurrentChange(string? password) => PasswordPolicyMirror(password);
static bool PasswordPolicyMirror(string? password) => !string.IsNullOrEmpty(password)
    && password.Length >= 8 && password.Length <= 128
    && password.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
    && password.Any(c => c is >= '0' and <= '9');
static bool CurrentBrowserRegistration(string? password) => !string.IsNullOrEmpty(password) && password.Length >= 8
    && password.Length <= 128
    && password.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') && password.Any(c => c is >= '0' and <= '9');
