using System.Text.Json;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// One-shot live check: upload + delete a 1px PNG through the real AvatarStorageService.
// Reads B2 settings from local User Secrets only. Never prints secrets.
var secretsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "Microsoft", "UserSecrets", "GlamByMariya-V2-Independent", "secrets.json");
using var doc = JsonDocument.Parse(File.ReadAllText(secretsPath));
string Get(string key) => doc.RootElement.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Supabase:Url"] = "https://independent-placeholder.supabase.co",
    ["SupabaseStorage:ServiceKey"] = Get("SupabaseStorage:ServiceKey"),
    ["B2:Bucket"] = Get("B2:Bucket"),
    ["B2:Endpoint"] = Get("B2:Endpoint"),
    ["B2:KeyId"] = Get("B2:KeyId"),
    ["B2:AppKey"] = Get("B2:AppKey"),
}).Build();

var factory = new StubFactory();
var service = new AvatarStorageService(factory, config, NullLogger<AvatarStorageService>.Instance);
Console.WriteLine($"provider: {service.Provider} configured={service.IsConfigured}");

var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
using var stream = new MemoryStream(png);
var file = new FormFile(stream, 0, png.Length, "avatar", "diag.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
var userId = Guid.NewGuid();
var url = await service.Upload(userId, file);
Console.WriteLine($"upload: ok len={url.Length}");
await service.Delete(userId, url);
Console.WriteLine("delete: ok");
Console.WriteLine("B2 AVATAR CHECK PASSED");

sealed class StubFactory : System.Net.Http.IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
