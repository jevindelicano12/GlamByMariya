using ImageMagick;
using GlamByMariya.Infrastructure.Services;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

static void Reject(byte[] bytes, string label)
{
    try { WebsiteImageValidator.Normalize(bytes); throw new Exception("Accepted " + label); }
    catch (InvalidOperationException) { Console.WriteLine("PASS rejected " + label); }
}
foreach (var format in new[] { MagickFormat.Png, MagickFormat.Jpeg })
{
    using var image = new MagickImage(MagickColors.RosyBrown, 12, 10);
    image.Format = format;
    var normalized = WebsiteImageValidator.Normalize(image.ToByteArray());
    if (normalized.Width != 12 || normalized.Height != 10 || normalized.Bytes.Length == 0) throw new Exception("Invalid normalization");
    Console.WriteLine("PASS normalized " + format);
}
Reject([], "empty image");
Reject(new byte[5_000_001], "oversized bytes");
Reject([0xff, 0xd8, 0xff, 1, 2, 3], "truncated JPEG");
Reject(System.Text.Encoding.UTF8.GetBytes("<svg></svg>"), "SVG");
using (var big = new MagickImage(MagickColors.White, 4001, 1)) { big.Format = MagickFormat.Png; Reject(big.ToByteArray(), "oversized dimensions"); }
var store = new FakeStore();
var service = new WebsitePrivateImageStore(store);
ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)], "test"));
using var sample = new MagickImage(MagickColors.White, 5, 5); sample.Format = MagickFormat.Png;
try { service.Save(Principal("Admin"), sample.ToByteArray()); throw new Exception("Admin upload accepted"); }
catch (UnauthorizedAccessException) { Console.WriteLine("PASS Admin denied before storage"); }
if (store.Writes != 0) throw new Exception("Unauthorized storage write");
var result = service.Save(Principal("SuperAdmin"), sample.ToByteArray());
if (!System.Text.RegularExpressions.Regex.IsMatch(result.Key, @"\Awebsite-content/[0-9a-f]{32}\.png\z") || store.Writes != 1) throw new Exception("Invalid key");
Console.WriteLine("PASS owner upload uses generated key and fake storage");
store.Fail = true;
try { service.Save(Principal("SuperAdmin"), sample.ToByteArray()); throw new Exception("Storage failure ignored"); }
catch (IOException) { Console.WriteLine("PASS storage failure propagated"); }
var temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "website-image-storage-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
var local = new LocalAppFileStore(new TestEnvironment { ContentRootPath = temp });
var localService = new WebsitePrivateImageStore(local);
var stored = localService.Save(Principal("SuperAdmin"), sample.ToByteArray());
var read = local.Read(stored.Key);
if (read == null || read.Value.ContentType != "image/png" || read.Value.Bytes.Length != stored.ByteLength) throw new Exception("Local image storage round trip failed");
Console.WriteLine("PASS normalized image round trip through isolated LocalAppFileStore");
var lookup = new TestPublishedLookup();
var publicImages = new WebsitePublicImageService(lookup, local);
var imageId = Guid.NewGuid();
if (await publicImages.Read(imageId) != null) throw new Exception("Missing published image was returned");
lookup.Asset = new(stored.Key, stored.ContentType, stored.ByteLength);
if ((await publicImages.Read(imageId))?.ContentType != "image/png") throw new Exception("Published image read failed");
lookup.Asset = new("../unsafe.png", "image/png", stored.ByteLength);
if (await publicImages.Read(imageId) != null) throw new Exception("Unsafe image key accepted");
lookup.Asset = new(stored.Key, "image/jpeg", stored.ByteLength);
if (await publicImages.Read(imageId) != null) throw new Exception("Incorrect content type accepted");
lookup.Asset = new(stored.Key, stored.ContentType, stored.ByteLength + 1);
if (await publicImages.Read(imageId) != null) throw new Exception("Incorrect asset length accepted");
Console.WriteLine("PASS published image reader uses mock lookup and isolated local storage, rejecting invalid metadata");
var draftLookup = new TestDraftLookup { Asset = new(stored.Key, stored.ContentType, stored.ByteLength) };
var draftImages = new WebsiteDraftImageService(draftLookup, local);
foreach (var principal in new[] { Principal("Admin"), Principal("Customer"), new ClaimsPrincipal() })
{
    try { await draftImages.Read(principal, imageId); throw new Exception("Non-owner draft read accepted"); }
    catch (UnauthorizedAccessException) { }
}
if (draftLookup.Calls != 0) throw new Exception("Unauthorized draft lookup");
if ((await draftImages.Read(Principal("SuperAdmin"), imageId))?.ContentType != "image/png") throw new Exception("Draft image read failed");
draftLookup.Asset = new("../unsafe.png", "image/png", stored.ByteLength);
if (await draftImages.Read(Principal("SuperAdmin"), imageId) != null) throw new Exception("Unsafe draft image key accepted");
draftLookup.Asset = null;
if (await draftImages.Read(Principal("SuperAdmin"), imageId) != null) throw new Exception("Missing draft image returned");
Console.WriteLine("PASS private draft image reader denies non-owners and rejects missing or unsafe assets");
Console.WriteLine("Isolated local image retained at " + temp);

sealed class FakeStore : IAppFileStore
{
    public int Writes { get; private set; }
    public bool Fail { get; set; }
    public void Save(string key, byte[] bytes) { if (Fail) throw new IOException("synthetic failure"); Writes++; }
    public (byte[] Bytes, string ContentType)? Read(string? key) => null;
    public void Delete(string? key) => throw new Exception("Delete must not be called");
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "WebsiteImageChecks";
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string WebRootPath { get; set; } = "";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

sealed class TestPublishedLookup : IWebsitePublishedAssetLookup
{
    public WebsitePublishedAsset? Asset { get; set; }
    public Task<WebsitePublishedAsset?> FindPublishedImage(Guid imageId, CancellationToken cancellationToken) => Task.FromResult(Asset);
}

sealed class TestDraftLookup : IWebsiteDraftAssetLookup
{
    public WebsitePublishedAsset? Asset { get; set; }
    public int Calls { get; private set; }
    public Task<WebsitePublishedAsset?> FindDraftImage(Guid imageId, CancellationToken cancellationToken) { Calls++; return Task.FromResult(Asset); }
}
