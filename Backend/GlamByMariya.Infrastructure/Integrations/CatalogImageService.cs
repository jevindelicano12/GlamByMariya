using GlamByMariya.Infrastructure.Data;
namespace GlamByMariya.Infrastructure.Integrations;

// Admin-uploaded catalog pictures persist locally; no database is needed.
// Files live outside wwwroot and are served publicly via CatalogImagesController.
public class CatalogImageService(string contentRoot, IPersistentStudioStore? database = null, DemoCatalogMedia? demoMedia = null)
{
    private readonly object gate = new();
    private readonly string folder = Path.Combine(contentRoot, "App_Data", "CatalogImages");
    private static void Validate(string kind, Guid id, string slot)
    {
        if (kind != "service" && kind != "addon") throw new InvalidOperationException("Invalid catalog image.");
        if (id == Guid.Empty) throw new InvalidOperationException("Invalid catalog image.");
        if (slot != "card" && slot != "preview") throw new InvalidOperationException("Invalid catalog image.");
    }
    private string Key(string kind, Guid id, string slot) => slot == "preview" ? kind + "-preview-" + id.ToString("N") : kind + "-" + id.ToString("N");
    private string? ImagePath(string key) => new[] { ".png", ".jpg" }
        .Select(extension => Path.Combine(folder, key + extension)).FirstOrDefault(File.Exists);
    public string? GetImageUrl(string kind, Guid id, string slot = "card")
    {
        Validate(kind, id, slot);
        if (demoMedia != null)
        {
            var version = demoMedia.Version(kind, id, slot);
            return version == null ? null : $"/CatalogImages/Image?kind={kind}&id={id:D}&slot={slot}&v={version}";
        }
        if (database != null)
        {
            var key = database.CatalogImageKey(kind, id, slot);
            return key == null ? null : $"/CatalogImages/Image?kind={kind}&id={id:D}&slot={slot}&v={Uri.EscapeDataString(key)}";
        }
        lock (gate)
        {
            var path = ImagePath(Key(kind, id, slot));
            if (path == null) return null;
            var url = $"/CatalogImages/Image?kind={kind}&id={id:D}&v={File.GetLastWriteTimeUtc(path).Ticks}";
            return slot == "preview" ? url + "&slot=preview" : url;
        }
    }
    public void Save(string kind, Guid id, byte[] bytes, string slot = "card")
    {
        Validate(kind, id, slot);
        if (demoMedia != null) { demoMedia.Save(kind, id, slot, bytes); return; }
        if (database != null) { database.SaveCatalogImage(kind, id, slot, bytes); return; }
        if (bytes.Length == 0 || bytes.Length > 5_000_000) throw new InvalidOperationException("Choose a JPG or PNG image up to 5 MB.");
        var png = bytes.Length > 8 && bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpg = bytes.Length > 3 && bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        if (!png && !jpg) throw new InvalidOperationException("Upload a JPG or PNG image. SVG and other file types are not supported.");
        lock (gate)
        {
            Directory.CreateDirectory(folder);
            var key = Key(kind, id, slot);
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
    public void Remove(string kind, Guid id, string slot = "card")
    {
        Validate(kind, id, slot);
        if (demoMedia != null) { demoMedia.Remove(kind, id, slot); return; }
        if (database != null) { database.RemoveCatalogImage(kind, id, slot); return; }
        lock (gate)
        {
            var key = Key(kind, id, slot);
            foreach (var extension in new[] { ".png", ".jpg" })
            {
                var path = Path.Combine(folder, key + extension);
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
    public (byte[] Bytes, string ContentType)? Read(string kind, Guid id, string slot = "card")
    {
        Validate(kind, id, slot);
        if (demoMedia != null) return demoMedia.Read(kind, id, slot);
        if (database != null) return database.ReadAsset(database.CatalogImageKey(kind, id, slot));
        lock (gate)
        {
            var path = ImagePath(Key(kind, id, slot));
            return path == null ? null : (File.ReadAllBytes(path), path.EndsWith(".png") ? "image/png" : "image/jpeg");
        }
    }
}
