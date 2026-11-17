using System.Text.Json;

namespace GlamByMariya.Infrastructure.Integrations;

public sealed record DemoGalleryPhoto(Guid Id, string Extension, string AltText, string Caption, bool Visible);

// Single-process, local demonstration storage; not a production content repository.
public sealed class DemoGalleryStore
{
    private readonly string root;
    private readonly object gate = new();
    public DemoGalleryStore(string root) { this.root = root; Directory.CreateDirectory(root); }
    private string Metadata(Guid id) => Path.Combine(root, id.ToString("N") + ".json");
    private string Image(DemoGalleryPhoto photo) => Path.Combine(root, photo.Id.ToString("N") + photo.Extension);

    public IReadOnlyList<DemoGalleryPhoto> List()
    {
        lock (gate)
        {
            var result = new List<DemoGalleryPhoto>();
            foreach (var path in Directory.EnumerateFiles(root, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) continue;
                try
                {
                    if (new FileInfo(path).Length > 4096) continue;
                    var photo = JsonSerializer.Deserialize<DemoGalleryPhoto>(File.ReadAllText(path));
                    if (photo != null && photo.Id == id && photo.Extension is ".jpg" or ".png" &&
                        !string.IsNullOrWhiteSpace(photo.AltText) && photo.AltText.Length <= 250 &&
                        photo.Caption != null && photo.Caption.Length <= 500 && File.Exists(Image(photo))) result.Add(photo);
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
            }
            return result.OrderBy(x => x.Id).ToArray();
        }
    }

    public DemoGalleryPhoto Upload(byte[] bytes, string? altText, string? caption)
    {
        altText = altText?.Trim(); caption = caption?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(altText) || altText.Length > 250 || caption.Length > 500)
            throw new InvalidOperationException("Provide alternative text (up to 250 characters) and a caption up to 500 characters.");
        var image = WebsiteImageValidator.Normalize(bytes);
        var photo = new DemoGalleryPhoto(Guid.NewGuid(), image.Extension, altText, caption, true);
        lock (gate)
        {
            using (var file = new FileStream(Image(photo), FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(image.Bytes);
            using var metadata = new FileStream(Metadata(photo.Id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(metadata, photo);
        }
        return photo;
    }

    public void SetVisibility(Guid id, bool visible)
    {
        lock (gate)
        {
            var photo = List().SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Photo not found.");
            var temp = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, photo with { Visible = visible });
            File.Move(temp, Metadata(id), true);
        }
    }

    public (byte[] Bytes, string Type)? Read(Guid id, bool includeHidden = false)
    {
        lock (gate)
        {
            var photo = List().SingleOrDefault(x => x.Id == id && (includeHidden || x.Visible));
            if (photo == null) return null;
            var path = Image(photo);
            if (new FileInfo(path).Length > WebsiteContentRules.MaxImageBytes) return null;
            return (File.ReadAllBytes(path), photo.Extension == ".png" ? "image/png" : "image/jpeg");
        }
    }
}
