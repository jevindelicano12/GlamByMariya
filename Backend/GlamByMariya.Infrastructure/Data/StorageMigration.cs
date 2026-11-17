using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

// One-shot move of App_Data file bytes (Proofs, PaymentQr, CatalogImages) to
// the private Supabase Storage bucket, preserving object keys so existing
// database references keep working. Read-only toward the database.
public static class StorageMigration
{
    public static async Task RunAsync(WebApplication app)
    {
        var config = app.Configuration;
        var serviceKey = config["SupabaseStorage:ServiceKey"];
        var baseUrl = (config["Supabase:Url"] ?? "").TrimEnd('/');
        if (string.IsNullOrWhiteSpace(serviceKey) || !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Storage migration needs Supabase:Url and SupabaseStorage:ServiceKey (server secrets, never chat).");
        using var scope = app.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        using var client = factory.CreateClient();
        client.BaseAddress = new Uri(baseUrl + "/storage/v1/");
        client.Timeout = TimeSpan.FromSeconds(60);
        client.DefaultRequestHeaders.Add("apikey", serviceKey);
        if (!serviceKey.StartsWith("sb_secret_", StringComparison.Ordinal)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);

        // Private bucket: all reads go through our authenticated controllers.
        var bucketBody = JsonSerializer.Serialize(new
        {
            id = SupabaseAppFileStore.Bucket,
            name = SupabaseAppFileStore.Bucket,
            @public = false,
            file_size_limit = 5242880,
            allowed_mime_types = new[] { "image/jpeg", "image/png" },
        });
        using (var buckets = await client.GetAsync("bucket"))
        {
            buckets.EnsureSuccessStatusCode();
            var list = await buckets.Content.ReadAsStringAsync();
            if (!list.Contains($"\"id\":\"{SupabaseAppFileStore.Bucket}\"", StringComparison.Ordinal))
            {
                using var created = await client.PostAsync("bucket",
                    new StringContent(bucketBody, Encoding.UTF8, "application/json"));
                created.EnsureSuccessStatusCode();
                Console.WriteLine($"Created private bucket {SupabaseAppFileStore.Bucket}.");
            }
        }

        var root = Path.Combine(app.Environment.ContentRootPath, "App_Data");
        var uploaded = 0;
        foreach (var folder in new[] { "Proofs", "PaymentQr", "CatalogImages" })
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.GetFiles(dir))
            {
                var key = folder + "/" + Path.GetFileName(file);
                var bytes = await File.ReadAllBytesAsync(file);
                using var body = new ByteArrayContent(bytes);
                body.Headers.ContentType = new MediaTypeHeaderValue(
                    key.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
                using var response = await client.PostAsync("object/" + SupabaseAppFileStore.Bucket + "/" + key, body);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException($"Upload of {key} failed ({(int)response.StatusCode}). Fix Storage and retry; nothing was changed locally.");
                uploaded++;
                Console.WriteLine($"Uploaded {key} ({bytes.Length} bytes).");
            }
        }
        Console.WriteLine($"Storage migration complete: {uploaded} file(s) in bucket {SupabaseAppFileStore.Bucket} with unchanged keys.");
    }
}
