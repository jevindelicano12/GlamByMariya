using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;

namespace GlamByMariya.Infrastructure.Integrations;

// Payment QR images persist locally; no database or payment processor is needed.
// Files are outside wwwroot and are served only to authenticated customers/admins.
public class PaymentQrService(string contentRoot, IPersistentStudioStore? database = null)
{
    private readonly object gate = new();
    private readonly string folder = Path.Combine(contentRoot, "App_Data", "PaymentQr");
    private static readonly Dictionary<string, string> Methods = new()
    {
        ["GCash"] = "gcash", ["Maya"] = "maya", ["Bank Transfer"] = "bank-transfer"
    };
    private string Key(string method) => method != null && Methods.TryGetValue(method, out var key)
        ? key : throw new InvalidOperationException("Choose GCash, Maya or Bank Transfer.");
    private string? ImagePath(string key) => new[] { ".png", ".jpg" }
        .Select(extension => Path.Combine(folder, key + extension)).FirstOrDefault(File.Exists);
    public List<PaymentQrOption> GetOptions()
    {
        if (database != null) return Methods.Select(m => new PaymentQrOption(m.Key,
            database.PaymentQrKey(m.Key) is { } key ? $"/Payments/Qr?method={Uri.EscapeDataString(m.Key)}&v={Uri.EscapeDataString(key)}" : null)).ToList();
        lock (gate)
        {
            return Methods.Select(method =>
            {
                var path = ImagePath(method.Value);
                var url = path == null ? null : $"/Payments/Qr?method={Uri.EscapeDataString(method.Key)}&v={File.GetLastWriteTimeUtc(path).Ticks}";
                return new PaymentQrOption(method.Key, url);
            }).ToList();
        }
    }
    public void Save(string method, byte[] bytes)
    {
        var key = Key(method);
        if (database != null) { database.SavePaymentQr(method, bytes); return; }
        if (bytes.Length == 0 || bytes.Length > 5_000_000) throw new InvalidOperationException("Choose a QR image up to 5 MB.");
        var png = bytes.Length > 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpg = bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        if (!png && !jpg) throw new InvalidOperationException("Upload a JPG or PNG QR image. SVG and other file types are not supported.");
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, key + (png ? ".png" : ".jpg"));
            var temporary = Path.Combine(folder, Guid.NewGuid() + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, target, overwrite: true);
                var alternate = Path.Combine(folder, key + (png ? ".jpg" : ".png"));
                if (File.Exists(alternate)) File.Delete(alternate);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public void Remove(string method)
    {
        var key = Key(method);
        if (database != null) { database.RemovePaymentQr(method); return; }
        lock (gate)
        {
            foreach (var extension in new[] { ".png", ".jpg" })
            {
                var path = Path.Combine(folder, key + extension);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
    public (byte[] Bytes, string ContentType)? Read(string method)
    {
        var key = Key(method);
        if (database != null) return database.ReadAsset(database.PaymentQrKey(method));
        lock (gate)
        {
            var path = ImagePath(key);
            return path == null ? null : (File.ReadAllBytes(path), path.EndsWith(".png") ? "image/png" : "image/jpeg");
        }
    }
}
