using System.Net.Http.Headers;

namespace GlamByMariya.Infrastructure.Integrations;

// Object-key file storage. Database rows keep the same keys either way; only
// the byte location changes. Local disk for development; Supabase Storage
// (private app-files bucket) when FileStorage:Provider=Supabase with a server key.
public interface IAppFileStore
{
    void Save(string key, byte[] bytes);
    (byte[] Bytes, string ContentType)? Read(string? key);
    void Delete(string? key);
}

// A missing or failed storage response is never evidence that a payment may be changed.
public enum ProofObjectStatus { Present, Absent, Unknown }
public sealed record ProofObjectRead(ProofObjectStatus Status, byte[]? Bytes = null, string? ContentType = null);

public interface IProofObjectOutcomeStore
{
    Task<ProofObjectRead> ReadProofAsync(string key, CancellationToken ct = default);
    Task<ProofObjectStatus> DeleteProofAsync(string key, CancellationToken ct = default);
}

internal static class ProofObjectKeys
{
    internal static bool IsStagingKey(string? key)
    {
        if (key == null || !key.StartsWith("ProofStaging/", StringComparison.Ordinal)) return false;
        var name = key["ProofStaging/".Length..];
        return name.Length == 36 && (name.EndsWith(".png", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal))
            && Guid.TryParseExact(name[..32], "N", out var id) && id != Guid.Empty
            && string.Equals(name[..32], id.ToString("N"), StringComparison.Ordinal);
    }
}

public static class AppFileStoreFactory
{
    public static IAppFileStore Create(IConfiguration config, IWebHostEnvironment env, IHttpClientFactory factory, ILogger<SupabaseAppFileStore> log)
    {
        var provider = config["FileStorage:Provider"];
        if (string.IsNullOrWhiteSpace(provider) || string.Equals(provider, "Local", StringComparison.OrdinalIgnoreCase))
            return new LocalAppFileStore(env);
        if (!string.Equals(provider, "Supabase", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File storage provider must be Local or Supabase.");
        if (!SupabaseAppFileStore.IsConfigured(config))
            throw new InvalidOperationException("Supabase file storage is not configured.");
        return new SupabaseAppFileStore(factory, config, log);
    }
}

public sealed class LocalAppFileStore(IWebHostEnvironment env) : IAppFileStore, IProofObjectOutcomeStore
{
    private string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, "App_Data"));

    private string PathFor(string key)
    {
        var path = Path.GetFullPath(Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid private asset location.");
        return path;
    }

    public void Save(string key, byte[] bytes)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public (byte[] Bytes, string ContentType)? Read(string? key)
    {
        if (key == null) return null;
        var path = PathFor(key);
        return File.Exists(path)
            ? (File.ReadAllBytes(path), key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg")
            : null;
    }

    public void Delete(string? key)
    {
        if (key == null) return;
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task<ProofObjectRead> ReadProofAsync(string key, CancellationToken ct = default)
    {
        if (!ProofObjectKeys.IsStagingKey(key)) return new(ProofObjectStatus.Unknown);
        try
        {
            await using var stream = new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (stream.Length > PaymentProofImageValidator.MaximumBytes) return new(ProofObjectStatus.Unknown);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (memory.Length + count > PaymentProofImageValidator.MaximumBytes) return new(ProofObjectStatus.Unknown);
                memory.Write(buffer, 0, count);
            }
            return new(ProofObjectStatus.Present, memory.ToArray(), key.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return new(ProofObjectStatus.Absent); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { return new(ProofObjectStatus.Unknown); }
    }

    public async Task<ProofObjectStatus> DeleteProofAsync(string key, CancellationToken ct = default)
    {
        if (!ProofObjectKeys.IsStagingKey(key) || ct.IsCancellationRequested) return ProofObjectStatus.Unknown;
        try { File.Delete(PathFor(key)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ProofObjectStatus.Unknown; }
        return (await ReadProofAsync(key, ct)).Status;
    }
}

public sealed class SupabaseAppFileStore(IHttpClientFactory factory, IConfiguration config, ILogger<SupabaseAppFileStore> log) : IAppFileStore, IProofObjectOutcomeStore
{
    public const string Bucket = "app-files";

    private bool Ready => string.Equals(config["FileStorage:Provider"], "Supabase", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate((config["Supabase:Url"] ?? "").Trim().TrimEnd('/'), UriKind.Absolute, out var url) && url.Scheme == "https"
        && !string.IsNullOrWhiteSpace(config["SupabaseStorage:ServiceKey"]);

    private HttpClient Client()
    {
        if (!Ready) throw new InvalidOperationException("Supabase file storage is not configured.");
        var client = factory.CreateClient();
        client.BaseAddress = new Uri((config["Supabase:Url"] ?? "").Trim().TrimEnd('/') + "/storage/v1/");
        client.Timeout = TimeSpan.FromSeconds(30);
        // A key pasted into the host settings may carry a trailing new line, which HTTP headers refuse.
        var key = StorageKey.Clean(config["SupabaseStorage:ServiceKey"]);
        client.DefaultRequestHeaders.Add("apikey", key);
        if (!key.StartsWith("sb_secret_", StringComparison.Ordinal)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    public static bool IsConfigured(IConfiguration config) =>
        string.Equals(config["FileStorage:Provider"], "Supabase", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate((config["Supabase:Url"] ?? "").Trim().TrimEnd('/'), UriKind.Absolute, out var url) && url.Scheme == "https"
        && !string.IsNullOrWhiteSpace(config["SupabaseStorage:ServiceKey"]);

    public void Save(string key, byte[] bytes) => SaveAsync(key, bytes).GetAwaiter().GetResult();
    public (byte[] Bytes, string ContentType)? Read(string? key) => key == null ? null : ReadAsync(key).GetAwaiter().GetResult();
    public void Delete(string? key) { if (key != null) DeleteAsync(key).GetAwaiter().GetResult(); }

    public async Task<ProofObjectRead> ReadProofAsync(string key, CancellationToken ct = default)
    {
        if (!ProofObjectKeys.IsStagingKey(key)) return new(ProofObjectStatus.Unknown);
        try
        {
            using var client = Client();
            using var response = await client.GetAsync("object/" + Bucket + "/" + key, HttpCompletionOption.ResponseHeadersRead, ct);
            // A 404 may also mean a missing bucket or concealed access; provider semantics are unverified.
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentLength > PaymentProofImageValidator.MaximumBytes)
                return new(ProofObjectStatus.Unknown);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (memory.Length + count > PaymentProofImageValidator.MaximumBytes) return new(ProofObjectStatus.Unknown);
                memory.Write(buffer, 0, count);
            }
            return new(ProofObjectStatus.Present, memory.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        { return new(ProofObjectStatus.Unknown); }
    }

    public async Task<ProofObjectStatus> DeleteProofAsync(string key, CancellationToken ct = default)
    {
        if (!ProofObjectKeys.IsStagingKey(key)) return ProofObjectStatus.Unknown;
        try
        {
            using var client = Client();
            using var request = new HttpRequestMessage(HttpMethod.Delete, "object/" + Bucket)
            {
                Content = JsonContent.Create(new { prefixes = new[] { key } })
            };
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return ProofObjectStatus.Unknown;
            // An acknowledgement alone does not prove deletion.
            return (await ReadProofAsync(key, ct)).Status;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        { return ProofObjectStatus.Unknown; }
    }

    private async Task SaveAsync(string key, byte[] bytes)
    {
        using var client = Client();
        using var body = new ByteArrayContent(bytes);
        body.Headers.ContentType = new MediaTypeHeaderValue(key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
        HttpResponseMessage response;
        try { response = await client.PostAsync("object/" + Bucket + "/" + key, body); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Supabase file upload could not be confirmed.");
            throw new InvalidOperationException("Cloud file storage is temporarily unavailable. Please try again.");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Supabase file upload failed with status {Status}.", (int)response.StatusCode);
                throw new InvalidOperationException("Cloud file storage is temporarily unavailable. Please try again.");
            }
        }
    }

    private async Task<(byte[] Bytes, string ContentType)?> ReadAsync(string key)
    {
        try
        {
            using var client = Client();
            using var response = await client.GetAsync("object/" + Bucket + "/" + key);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var mime = response.Content.Headers.ContentType?.MediaType
                ?? (key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            return (bytes, mime);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            log.LogWarning("Supabase file download failed.");
            return null;
        }
    }

    private async Task DeleteAsync(string key)
    {
        try
        {
            using var client = Client();
            using var request = new HttpRequestMessage(HttpMethod.Delete, "object/" + Bucket)
            {
                Content = JsonContent.Create(new { prefixes = new[] { key } })
            };
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) log.LogWarning("Supabase file delete failed with status {Status}.", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        { log.LogWarning("Supabase file delete could not reach Storage."); }
    }
}
