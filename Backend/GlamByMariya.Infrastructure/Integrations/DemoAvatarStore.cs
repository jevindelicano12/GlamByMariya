using GlamByMariya.Core.Entities;

namespace GlamByMariya.Infrastructure.Integrations;

// Presentation identities change GUID on restart. Restrict durable local avatars to
// the three source-defined presentation role/email pairs, never arbitrary accounts.
public sealed class DemoAvatarStore(string root, bool presentationAccounts = false)
{
    private readonly object gate = new();
    private string? Key(AppUser user) => (presentationAccounts, user.Role, user.Email.ToLowerInvariant()) switch
    {
        (true, "Customer", "presentation-customer@example.test") => "customer",
        (true, "Admin", "presentation-admin@example.test") => "admin",
        (true, "SuperAdmin", "presentation-owner@example.test") => "owner",
        _ => null
    };
    public bool Supports(AppUser user) => Key(user) != null;
    private string Pointer(string key) => Path.Combine(root, key + ".txt");
    private string? Name(AppUser user)
    {
        var key = Key(user);
        if (key == null || !File.Exists(Pointer(key))) return null;
        if (new FileInfo(Pointer(key)).Length > 64) return null;
        var name = File.ReadAllText(Pointer(key));
        return System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[a-f0-9]{32}\.(jpg|png)\z") ? name : null;
    }
    public bool Has(AppUser user) { lock (gate) { var name = Name(user); return name != null && File.Exists(Path.Combine(root, name)); } }
    public void Save(AppUser user, IFormFile? file)
    {
        var key = Key(user) ?? throw new InvalidOperationException("Local demo profile uploads are available only for the seeded demo accounts.");
        if (file == null || file.Length < 12 || file.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Choose a JPG or PNG profile picture up to 2 MB.");
        using var memory = new MemoryStream(); file.CopyTo(memory);
        var image = WebsiteImageValidator.Normalize(memory.ToArray());
        if (image.Bytes.Length > 2 * 1024 * 1024) throw new InvalidOperationException("Processed profile picture exceeds 2 MB.");
        lock (gate)
        {
            Directory.CreateDirectory(root);
            var name = Guid.NewGuid().ToString("N") + image.Extension;
            using (var output = new FileStream(Path.Combine(root, name), FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(image.Bytes);
            SetPointer(key, name);
        }
    }
    private void SetPointer(string key, string value)
    {
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        using (var output = new StreamWriter(new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))) output.Write(value);
        File.Move(temporary, Pointer(key), true);
    }
    public void Remove(AppUser user) { var key = Key(user) ?? throw new InvalidOperationException("This demo account is not supported."); lock (gate) SetPointer(key, ""); }
    public (byte[] Bytes, string Type)? Read(AppUser user)
    {
        lock (gate)
        {
            var name = Name(user); if (name == null) return null;
            var path = Path.Combine(root, name); if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024) return null;
            return (File.ReadAllBytes(path), name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
        }
    }
}
