using System.Net.Http.Headers;
using Amazon.S3;
using Amazon.S3.Model;

namespace GlamByMariya.Infrastructure.Integrations;

// Server-only credentials. Public objects are pictures only; writes require our authenticated controller.
// Provider B2 (Backblaze, S3-compatible) is used when B2 settings are present;
// otherwise Supabase Storage is used when its server key is present.
public sealed class AvatarStorageService(IHttpClientFactory factory, IConfiguration config, ILogger<AvatarStorageService> log)
{
    public string Provider =>
        !string.IsNullOrWhiteSpace(config["B2:Bucket"]) && !string.IsNullOrWhiteSpace(config["B2:Endpoint"])
        && !string.IsNullOrWhiteSpace(config["B2:KeyId"]) && !string.IsNullOrWhiteSpace(config["B2:AppKey"]) ? "B2"
        : SupabaseReady ? "Supabase" : "None";

    private bool SupabaseReady => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) && url.Scheme == "https"
        && !string.IsNullOrWhiteSpace(config["SupabaseStorage:ServiceKey"]);

    public bool IsConfigured => Provider is "B2" or "Supabase";

    private string BaseUrl => (config["Supabase:Url"] ?? "").TrimEnd('/');
    private const string Bucket = "profile-avatars";

    private static (byte[] Bytes, string Extension, string Mime) ReadImage(IFormFile? file)
    {
        if (file == null || file.Length < 12 || file.Length > 2 * 1024 * 1024)
            throw new InvalidOperationException("Choose a JPG or PNG profile picture up to 2 MB.");
        using var memory = new MemoryStream();
        file.CopyTo(memory);
        var bytes = memory.ToArray();
        var png = bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255;
        if (!png && !jpeg) throw new InvalidOperationException("Choose a valid JPG or PNG image.");
        return (bytes, png ? "png" : "jpg", png ? "image/png" : "image/jpeg");
    }

    public async Task<string> Upload(Guid userId, IFormFile? file)
    {
        var (bytes, ext, mime) = ReadImage(file);
        var path = $"{userId:D}/{Guid.NewGuid():N}.{ext}";
        return Provider switch
        {
            "B2" => await B2Upload(path, bytes, mime),
            "Supabase" => await SupabaseUpload(path, bytes, mime),
            _ => throw new InvalidOperationException("Profile uploads are not configured. Run scripts/Configure-B2AvatarStorage.ps1 or scripts/Configure-AvatarStorage.ps1 first."),
        };
    }

    public async Task Delete(Guid userId, string? url)
    {
        if (url == null) return;
        try
        {
            if (Provider == "B2") await B2Delete(userId, url);
            else if (Provider == "Supabase") await SupabaseDelete(userId, url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or AmazonS3Exception or InvalidOperationException)
        { log.LogWarning("Old avatar cleanup failed."); }
    }

    private AmazonS3Client B2Client() => new(
        config["B2:KeyId"], config["B2:AppKey"],
        new AmazonS3Config { ServiceURL = config["B2:Endpoint"]!.TrimEnd('/'), ForcePathStyle = true, Timeout = TimeSpan.FromSeconds(30) });

    private async Task<string> B2Upload(string path, byte[] bytes, string mime)
    {
        using var client = B2Client();
        using var stream = new MemoryStream(bytes, writable: false);
        var response = await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = config["B2:Bucket"],
            Key = path,
            InputStream = stream,
            ContentType = mime,
            // No CannedACL: B2 takes public/private from the bucket setting, not object ACLs.
        });
        if ((int)response.HttpStatusCode is < 200 or >= 300)
            throw new InvalidOperationException("Cloud upload failed. Check the B2 bucket, endpoint and keys.");
        return $"{config["B2:Endpoint"]!.TrimEnd('/')}/{config["B2:Bucket"]}/{path}";
    }

    private async Task B2Delete(Guid userId, string? url)
    {
        if (url == null) return;
        var prefix = $"{config["B2:Endpoint"]!.TrimEnd('/')}/{config["B2:Bucket"]}/{userId:D}/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return;
        var name = url[prefix.Length..];
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-f0-9]{32}\\.(jpg|png)$")) return;
        using var client = B2Client();
        await client.DeleteObjectAsync(config["B2:Bucket"], $"{userId:D}/{name}");
    }

    private HttpClient SupabaseClient()
    {
        if (!SupabaseReady) throw new InvalidOperationException("Profile uploads need the Supabase Storage server key. Run scripts/Configure-AvatarStorage.ps1 first.");
        var client = factory.CreateClient();
        client.BaseAddress = new Uri(BaseUrl + "/storage/v1/");
        client.Timeout = TimeSpan.FromSeconds(30);
        var key = config["SupabaseStorage:ServiceKey"]!;
        client.DefaultRequestHeaders.Add("apikey", key);
        if (!key.StartsWith("sb_secret_", StringComparison.Ordinal)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    private async Task<string> SupabaseUpload(string path, byte[] bytes, string mime)
    {
        using var client = SupabaseClient();
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue(mime);
        using var response = await client.PostAsync("object/" + Bucket + "/" + path, body);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Cloud upload failed. Check the Storage key and profile-avatars bucket configuration.");
        return BaseUrl + "/storage/v1/object/public/" + Bucket + "/" + path;
    }

    private async Task SupabaseDelete(Guid userId, string? url)
    {
        if (url == null) return;
        var prefix = BaseUrl + "/storage/v1/object/public/" + Bucket + "/" + userId.ToString("D") + "/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) return;
        var name = url[prefix.Length..];
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-f0-9]{32}\\.(jpg|png)$")) return;
        using var client = SupabaseClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "object/" + Bucket)
        {
            Content = JsonContent.Create(new { prefixes = new[] { userId.ToString("D") + "/" + name } })
        };
        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode) log.LogWarning("Old avatar cleanup failed with status {Status}.", (int)response.StatusCode);
    }
}
