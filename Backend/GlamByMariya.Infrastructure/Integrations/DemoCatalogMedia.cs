using System.Text.Json;
using GlamByMariya.Infrastructure.Data;

namespace GlamByMariya.Infrastructure.Integrations;

// Each pointer is bound to an existing catalog GUID and kind. Image files are
// immutable; replacing or removing a pointer never deletes older image bytes.
public sealed class DemoCatalogMedia(string root, DemoStore store)
{
    private sealed record Pointer(Guid Id, string Kind, string Slot, Guid AssetId, string Extension);
    private string PointerPath(string kind, Guid id, string slot) => Path.Combine(root, $"{kind}-{id:N}-{slot}.json");
    private string AssetPath(Pointer pointer) => Path.Combine(root, pointer.AssetId.ToString("N") + pointer.Extension);
    private bool Exists(string kind, Guid id) => (kind == "service" ? store.Services : store.AddOns).Any(x => x.Id == id);
    private Pointer? Find(string kind, Guid id, string slot)
    {
        if (!Exists(kind, id)) return null;
        var file = PointerPath(kind, id, slot);
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length > 2048) return null;
            var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(file));
            if (pointer == null || pointer.Id != id || pointer.Kind != kind || pointer.Slot != slot || pointer.AssetId == Guid.Empty || pointer.Extension is not (".png" or ".jpg")) return null;
            var asset = AssetPath(pointer);
            return File.Exists(asset) && new FileInfo(asset).Length is > 0 and <= WebsiteContentRules.MaxImageBytes ? pointer : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }
    public string? Version(string kind, Guid id, string slot)
    {
        lock (store.Gate) return Find(kind, id, slot)?.AssetId.ToString("N");
    }
    public (byte[] Bytes, string ContentType)? Read(string kind, Guid id, string slot)
    {
        lock (store.Gate)
        {
            var pointer = Find(kind, id, slot); if (pointer == null) return null;
            try { return (File.ReadAllBytes(AssetPath(pointer)), pointer.Extension == ".png" ? "image/png" : "image/jpeg"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }
    public void Save(string kind, Guid id, string slot, byte[] bytes)
    {
        var image = WebsiteImageValidator.Normalize(bytes);
        lock (store.Gate)
        {
            if (!Exists(kind, id)) throw new InvalidOperationException("Catalog item not found.");
            try
            {
                Directory.CreateDirectory(root);
                var pointer = new Pointer(id, kind, slot, Guid.NewGuid(), image.Extension);
                using (var file = new FileStream(AssetPath(pointer), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(image.Bytes); file.Flush(true); }
                CommitPointer(kind, id, slot, pointer);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("Demo image save could not be confirmed.", e); }
        }
    }
    private void CommitPointer(string kind, Guid id, string slot, Pointer? pointer)
    {
        Directory.CreateDirectory(root);
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, pointer); file.Flush(true); }
        File.Move(temporary, PointerPath(kind, id, slot), true);
    }
    public void Remove(string kind, Guid id, string slot)
    {
        lock (store.Gate)
        {
            try { CommitPointer(kind, id, slot, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException("Demo image removal could not be confirmed.", e); }
        }
    }
}
