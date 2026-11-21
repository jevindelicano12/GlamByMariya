using System.Security.Claims;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using ImageMagick;

var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "proof-storage-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var env = new TestEnvironment { ContentRootPath = root };
var store = new DemoStore(presentationAccounts: true);
var customer = store.Users.Single(x => x.Role == "Customer");
var other = store.AddUser("Other", "Customer", "other@example.test", "Customer123!", "Customer", 28, "Others", "24 Other Street");
var admin = store.Users.Single(x => x.Role == "Admin");
var booking = store.Bookings.Single(x => x.CustomerId == customer.Id);
var payment = store.Payments.Single(x => x.BookingId == booking.Id);
var proofPath = Path.Combine(root, "App_Data", "Proofs", "proof.png");
Directory.CreateDirectory(Path.GetDirectoryName(proofPath)!);
byte[] png;
using (var pngSource = new MagickImage(MagickColors.White, 8, 8))
{
    pngSource.Format = MagickFormat.Png;
    png = pngSource.ToByteArray();
}
await File.WriteAllBytesAsync(proofPath, png);
payment.ProofPath = proofPath;

var files = new LocalAppFileStore(env);
var normalizedPng = PaymentProofImageValidator.Normalize(png);
Check(normalizedPng.ContentType == "image/png" && normalizedPng.Bytes.Length <= PaymentProofImageValidator.MaximumBytes, "Valid PNG is decoded, normalized, and bounded");
using (var samePixels = new MagickImage(MagickColors.White, 8, 8))
using (var differentPixels = new MagickImage(MagickColors.Black, 8, 8))
using (var differentSize = new MagickImage(MagickColors.White, 9, 8))
{
    samePixels.Format = MagickFormat.Jpeg; samePixels.Quality = 100;
    differentPixels.Format = MagickFormat.Png; differentSize.Format = MagickFormat.Png;
    var fingerprint = PaymentProofImageValidator.PixelFingerprint(png);
    Check(fingerprint == PaymentProofImageValidator.PixelFingerprint(samePixels.ToByteArray()), "identical decoded pixels compare across PNG and JPEG encoding");
    Check(fingerprint != PaymentProofImageValidator.PixelFingerprint(differentPixels.ToByteArray()), "different decoded proof pixels differ");
    Check(fingerprint != PaymentProofImageValidator.PixelFingerprint(differentSize.ToByteArray()), "different proof dimensions differ");
}
using (var jpegSource = new MagickImage(MagickColors.Red, 32, 16))
{
    jpegSource.Format = MagickFormat.Jpeg;
    var jpeg = jpegSource.ToByteArray();
    var normalizedJpeg = PaymentProofImageValidator.Normalize(jpeg);
    Check(normalizedJpeg.ContentType == "image/jpeg" && normalizedJpeg.Extension == ".jpg", "Valid JPEG is decoded and re-encoded");
    using var normalizedImage = new MagickImage(normalizedJpeg.Bytes);
    Check(normalizedImage.GetExifProfile() == null, "Normalized image has no EXIF metadata");
}
foreach (var invalid in new[] { new byte[] { 137, 80, 78, 71, 1, 2, 3 }, new byte[] { 255, 216, 255, 0, 1 }, System.Text.Encoding.UTF8.GetBytes("not an image") })
{
    var rejected = false;
    try { PaymentProofImageValidator.Normalize(invalid); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Malformed or fake image is rejected");
    string? fingerprint = null;
    try { fingerprint = PaymentProofImageValidator.PixelFingerprint(invalid); throw new Exception("Malformed image produced a fingerprint"); }
    catch (InvalidOperationException e) when (e.Message == "Proof must be a valid, decodable JPG or PNG image.") { }
    Check(fingerprint == null, "Malformed image fingerprint fails with exact decoder error and produces no value");
}
string? emptyFingerprint = null;
try { emptyFingerprint = PaymentProofImageValidator.PixelFingerprint([]); throw new Exception("Empty image produced a fingerprint"); }
catch (InvalidOperationException e) when (e.Message == "Upload a JPG or PNG image up to 5 MB.") { }
Check(emptyFingerprint == null, "Empty image fingerprint fails with exact size error and produces no value");
var oversized = new byte[PaymentProofImageValidator.MaximumBytes + 1];
var oversizedRejected = false;
try { PaymentProofImageValidator.Normalize(oversized); } catch (InvalidOperationException) { oversizedRejected = true; }
Check(oversizedRejected, "Image over 5 MB is rejected");
using (var wide = new MagickImage(MagickColors.Blue, (uint)PaymentProofImageValidator.MaximumDimension + 1, 1))
{
    wide.Format = MagickFormat.Png;
    var dimensionRejected = false;
    try { PaymentProofImageValidator.Normalize(wide.ToByteArray()); } catch (InvalidOperationException) { dimensionRejected = true; }
    Check(dimensionRejected, "Image over the dimension limit is rejected");
}
var workflow = new BookingService(store, store, store, store, store);
var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
var images = new CatalogImageService(root);

ClaimsPrincipal Principal(AppUser user) => new(new ClaimsIdentity([
    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
    new Claim(ClaimTypes.Name, user.Name),
    new Claim(ClaimTypes.Email, user.Email),
    new Claim(ClaimTypes.Role, user.Role)], "proof-test"));
using var services = new ServiceCollection().AddSingleton<IWebHostEnvironment>(env).BuildServiceProvider();
void Context(Controller controller, AppUser user) => controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(user), RequestServices = services } };
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

var customerController = new BookingsController(store, workflow, env, images, files);
Context(customerController, customer);
customerController.Request.Headers.Accept = "application/json";
var beforeBookingState = System.Text.Json.JsonSerializer.Serialize(store.Bookings);
var beforePaymentState = System.Text.Json.JsonSerializer.Serialize(store.Payments);
var beforeProofFiles = Directory.GetFiles(Path.GetDirectoryName(proofPath)!).OrderBy(x => x).ToArray();
var invalidBytes = System.Text.Encoding.UTF8.GetBytes("not a decodable image");
using (var invalidStream = new MemoryStream(invalidBytes))
{
    var invalidProof = new FormFile(invalidStream, 0, invalidBytes.Length, "proof", "invalid.png");
    var serviceId = store.Services.First().Id;
    var selection = System.Text.Json.JsonSerializer.Serialize(new[] { new { serviceId, addOnIds = Array.Empty<Guid>() }, new { serviceId, addOnIds = Array.Empty<Guid>() } });
    var result = await customerController.CreateMixed(DateTime.Today.AddDays(30).AddHours(8), "Synthetic address", "Quezon City", 500, "GCash", "SYNTHETIC-INVALID", invalidProof, true, true, 1000, 500, selection, Guid.NewGuid(), store.Settings.PolicyVersion, store.Settings.TravelReminderVersion);
    Check(result is BadRequestObjectResult bad && bad.StatusCode == 400 && System.Text.Json.JsonSerializer.Serialize(bad.Value).Contains("Proof must be a valid, decodable JPG or PNG image."), "Invalid proof controller request returns exact decoder error and HTTP 400 result");
}
Check(beforeBookingState == System.Text.Json.JsonSerializer.Serialize(store.Bookings) && beforePaymentState == System.Text.Json.JsonSerializer.Serialize(store.Payments), "Invalid proof request preserves existing booking and payment state");
Check(beforeProofFiles.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(proofPath)!).OrderBy(x => x)) && payment.ProofPath == proofPath && File.ReadAllBytes(proofPath).SequenceEqual(png), "Invalid proof request retains original proof and creates no accepted proof file");
var own = customerController.Proof(payment.Id) as FileContentResult;
Check(own?.FileContents.SequenceEqual(png) == true, "Customer can retrieve proof for own booking");

Context(customerController, other);
Check(customerController.Proof(payment.Id) is NotFoundResult, "Customer cannot retrieve another customer's proof");

var anonymous = new BookingsController(store, workflow, env, images, files);
Context(anonymous, customer);
Check(anonymous.Proof(Guid.NewGuid()) is NotFoundResult, "Unknown proof is not disclosed");

var adminController = new AdminController(store, workflow, images, null!, null!);
Context(adminController, admin);
var adminProof = adminController.Proof(payment.Id);
var adminBytes = adminProof is PhysicalFileResult physical ? await File.ReadAllBytesAsync(physical.FileName) : adminProof is FileContentResult content ? content.FileContents : [];
Check(adminBytes.SequenceEqual(png), "Admin can retrieve authorized proof");
var persistent = System.Reflection.DispatchProxy.Create<IPersistentStudioStore, ProofReviewProxy>();
var reviewProxy = (ProofReviewProxy)persistent;
reviewProxy.Booking = booking;
reviewProxy.Admin = admin;
reviewProxy.ProofBytes = png;
reviewProxy.Payment = new Payment { Id=payment.Id, BookingId=booking.Id, ProofKey="Proofs/synthetic.png", PersistentReviewRevision=4, PersistentReviewVersion="42" };
var persistentAdmin = new AdminController(persistent, new BookingService(persistent,persistent,persistent,persistent,persistent), images, null!, null!);
Context(persistentAdmin, admin);
Check(persistentAdmin.Proof(payment.Id, booking.Id, 4, "42") is FileContentResult exactReview && exactReview.FileContents.SequenceEqual(png), "Persistent proof endpoint serves the exact reviewed snapshot in the fake store");
Check(persistentAdmin.Proof(payment.Id, booking.Id, 3, "42") is ConflictObjectResult, "Persistent proof endpoint rejects an old revision");
Check(persistentAdmin.Proof(payment.Id, booking.Id, 4, "43") is ConflictObjectResult, "Persistent proof endpoint rejects an old row version");
Check(persistentAdmin.Proof(payment.Id, booking.Id, 4, null) is ConflictObjectResult, "Persistent proof endpoint rejects a missing version");
Context(persistentAdmin, other);
Check(persistentAdmin.Proof(payment.Id, booking.Id, 4, "42") is ForbidResult, "Persistent proof endpoint rejects an unauthorized actor");

var persistentWorkflow = new BookingService(persistent, persistent, persistent, persistent, persistent);
var persistentCustomer = new BookingsController(persistent, persistentWorkflow, env, images, files);
Context(persistentCustomer, customer);
persistentCustomer.TempData = new TempDataDictionary(persistentCustomer.HttpContext, new EmptyTempDataProvider());
persistentCustomer.Url = new UrlHelper(new ActionContext(persistentCustomer.HttpContext, new RouteData(), new ActionDescriptor()));
await persistentCustomer.CorrectProof(booking.Id, "SYNTHETIC", null);
Check(persistentCustomer.TempData["Error"]?.ToString()?.Contains("Reload") == true, "Persistent correction rejects missing token");
persistentCustomer.ModelState.AddModelError("expectedPaymentRevision", "invalid");
await persistentCustomer.CorrectProof(booking.Id, "SYNTHETIC", null, expectedPaymentRevision: 4, expectedPaymentVersion: "42");
Check(persistentCustomer.TempData["Error"]?.ToString()?.Contains("Reload") == true, "Persistent correction rejects malformed model state");
persistentCustomer.ModelState.Clear();
await persistentCustomer.CorrectProof(booking.Id, "SYNTHETIC", null, expectedPaymentRevision: 4, expectedPaymentVersion: "42");
Check(persistentCustomer.TempData["Error"]?.ToString()?.Contains("unavailable") == true, "Persistent correction remains gated even with a valid token");
var filesBeforeGatedCorrection = Directory.GetFiles(Path.GetDirectoryName(proofPath)!).OrderBy(x => x).ToArray();
using (var gatedProofStream = new MemoryStream(png))
{
    var gatedProof = new FormFile(gatedProofStream, 0, png.Length, "proof", "synthetic.png");
    await persistentCustomer.CorrectProof(booking.Id, "SYNTHETIC", gatedProof, expectedPaymentRevision: 3, expectedPaymentVersion: "41");
}
Check(persistentCustomer.TempData["Error"]?.ToString()?.Contains("unavailable") == true &&
    filesBeforeGatedCorrection.SequenceEqual(Directory.GetFiles(Path.GetDirectoryName(proofPath)!).OrderBy(x => x)),
    "Persistent stale-looking correction remains gated without staging a proof");
var correctionBlocked = false;
try { persistentWorkflow.CorrectProof(booking, customer.Id, "SYNTHETIC", null, 4, "42"); } catch (InvalidOperationException e) { correctionBlocked = e.Message.Contains("unavailable"); }
Check(correctionBlocked, "Service blocks persistent correction before adapter access");
var reviewBlocked = false;
try { persistentWorkflow.AdminAction(booking, "verify", admin.Id, "", 0, "", paymentId: payment.Id, reviewRevision: 4, reviewVersion: "42"); } catch (InvalidOperationException e) { reviewBlocked = e.Message.Contains("unavailable"); }
Check(reviewBlocked, "Service blocks persistent payment review before adapter access");

var escaped = false;
try { files.Read("../outside"); } catch (InvalidOperationException) { escaped = true; }
Check(escaped, "Local private storage rejects path traversal");
var cloudConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["FileStorage:Provider"] = "Supabase",
    ["Supabase:Url"] = "https://storage.example.test",
    ["SupabaseStorage:ServiceKey"] = "test-only-service-key"
}).Build();
Check(AppFileStoreFactory.Create(cloudConfig, env, new FixedHttpClientFactory(), NullLogger<SupabaseAppFileStore>.Instance) is SupabaseAppFileStore,
    "configured cloud provider selects private cloud storage");
foreach (var missing in new[] { "Supabase:Url", "SupabaseStorage:ServiceKey" })
{
    var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["FileStorage:Provider"] = "Supabase", ["Supabase:Url"] = missing == "Supabase:Url" ? "http://storage.example.test" : "https://storage.example.test",
        ["SupabaseStorage:ServiceKey"] = missing == "SupabaseStorage:ServiceKey" ? "" : "test-only-service-key" }).Build();
    var denied = false;
    try { _ = AppFileStoreFactory.Create(invalid, env, new FixedHttpClientFactory(), NullLogger<SupabaseAppFileStore>.Instance); }
    catch (InvalidOperationException) { denied = true; }
    Check(denied, "incomplete Supabase configuration fails closed: " + missing);
}
Check(AppFileStoreFactory.Create(new ConfigurationBuilder().Build(), env, new FixedHttpClientFactory(), NullLogger<SupabaseAppFileStore>.Instance) is LocalAppFileStore,
    "default local Demo storage remains local");
Check(AppFileStoreFactory.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Provider"] = "Local" }).Build(), env, new FixedHttpClientFactory(), NullLogger<SupabaseAppFileStore>.Instance) is LocalAppFileStore,
    "explicit local Demo storage remains local");
var unknownProvider = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FileStorage:Provider"] = "Supabse" }).Build();
var unknownDenied = false;
try { _ = AppFileStoreFactory.Create(unknownProvider, env, new FixedHttpClientFactory(), NullLogger<SupabaseAppFileStore>.Instance); }
catch (InvalidOperationException) { unknownDenied = true; }
Check(unknownDenied, "unknown cloud provider cannot silently select local storage");
var failingCloud = new SupabaseAppFileStore(new FixedHttpClientFactory(), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
var cloudFailed = false;
try { failingCloud.Save("Proofs/new-proof.png", png); } catch (InvalidOperationException) { cloudFailed = true; }
Check(cloudFailed, "Cloud storage failure is reported without pretending the upload succeeded");
foreach (var timeout in new[] { false, true })
{
    var uncertainCloud = new SupabaseAppFileStore(new ThrowingHttpClientFactory(timeout), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
    var uncertainDenied = false;
    try { uncertainCloud.Save("Proofs/uncertain.png", png); }
    catch (InvalidOperationException e) { uncertainDenied = e.Message == "Cloud file storage is temporarily unavailable. Please try again." && e.InnerException == null; }
    Check(uncertainDenied, "Cloud " + (timeout ? "timeout" : "transport failure") + " returns generic failure without claiming upload success");
}
var recorded = new RecordingHandler();
var successfulCloud = new SupabaseAppFileStore(new RecordingHttpClientFactory(recorded), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
successfulCloud.Save("Proofs/new-transaction-proof.png", png);
// Simulate the controller's failed-transaction compensation: only the current
// operation's random key is deleted; no historical key is touched.
successfulCloud.Delete("Proofs/new-transaction-proof.png");
Check(recorded.Paths.Count == 2 && recorded.Paths[0].Contains("object/app-files/Proofs/new-transaction-proof.png") && recorded.Paths[1].Contains("object/app-files"), $"Failed transaction cleanup deletes only the newly uploaded object ({string.Join(",", recorded.Paths)})");
var stagingKey = "ProofStaging/" + Guid.NewGuid().ToString("N") + ".png";
files.Save(stagingKey, png);
var localRead = await files.ReadProofAsync(stagingKey);
Check(localRead.Status == ProofObjectStatus.Present && localRead.Bytes!.SequenceEqual(png), "local proof read reports present bytes");
Check(await files.DeleteProofAsync(stagingKey) == ProofObjectStatus.Absent &&
      (await files.ReadProofAsync(stagingKey)).Status == ProofObjectStatus.Absent, "local proof delete verifies absence");
Check((await files.ReadProofAsync("../outside")).Status == ProofObjectStatus.Unknown &&
      await files.DeleteProofAsync("Proofs/proof.png") == ProofObjectStatus.Unknown && File.Exists(proofPath),
      "outcome operations reject arbitrary keys and do not delete historical proof");
var missingCloud = new SupabaseAppFileStore(new StatusHttpClientFactory(HttpStatusCode.NotFound), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
Check((await missingCloud.ReadProofAsync(stagingKey)).Status == ProofObjectStatus.Unknown, "cloud 404 stays unknown until provider absence semantics are verified");
var emptyResponseCloud = new SupabaseAppFileStore(new StatusHttpClientFactory(HttpStatusCode.NoContent), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
Check((await emptyResponseCloud.ReadProofAsync(stagingKey)).Status == ProofObjectStatus.Unknown, "non-object success is not treated as a verified read");
var failingOutcomes = new SupabaseAppFileStore(new StatusHttpClientFactory(HttpStatusCode.InternalServerError), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
Check((await failingOutcomes.ReadProofAsync(stagingKey)).Status == ProofObjectStatus.Unknown &&
      await failingOutcomes.DeleteProofAsync(stagingKey) == ProofObjectStatus.Unknown, "cloud errors cannot confirm absence or deletion");
var timedOutcomes = new SupabaseAppFileStore(new ThrowingHttpClientFactory(true), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
Check((await timedOutcomes.ReadProofAsync(stagingKey)).Status == ProofObjectStatus.Unknown &&
      await timedOutcomes.DeleteProofAsync(stagingKey) == ProofObjectStatus.Unknown, "cloud timeouts have unknown outcome");
var acknowledged = new SupabaseAppFileStore(new StatusHttpClientFactory(HttpStatusCode.OK), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
Check(await acknowledged.DeleteProofAsync(stagingKey) == ProofObjectStatus.Present, "delete acknowledgement alone does not establish absence");
var acknowledgedThenMissing = new SupabaseAppFileStore(new SequenceHttpClientFactory(HttpStatusCode.OK, HttpStatusCode.NotFound), cloudConfig, NullLogger<SupabaseAppFileStore>.Instance);
Check(await acknowledgedThenMissing.DeleteProofAsync(stagingKey) == ProofObjectStatus.Unknown,
    "successful delete followed by cloud 404 does not falsely confirm absence");
files.Delete("Proofs/proof.png");
Check(!File.Exists(proofPath), "Only the test-created proof was cleaned up");
try { Directory.Delete(root, true); } catch { }
Console.WriteLine("All proof storage checks passed.");

sealed class FixedHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new FailingHandler());
}

sealed class StatusHttpClientFactory(HttpStatusCode status) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new StatusHandler(status));
}

sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent([1, 2, 3]) });
}

sealed class SequenceHttpClientFactory(params HttpStatusCode[] responses) : IHttpClientFactory
{
    private readonly Queue<HttpStatusCode> responses = new(responses);
    public HttpClient CreateClient(string name) => new(new SequenceHandler(responses));
}

sealed class SequenceHandler(Queue<HttpStatusCode> responses) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(responses.Dequeue()) { Content = new ByteArrayContent([1, 2, 3]) });
}

sealed class EmptyTempDataProvider : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}

sealed class ThrowingHttpClientFactory(bool timeout) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new ThrowingHandler(timeout));
}

sealed class ThrowingHandler(bool timeout) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        timeout ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("synthetic"))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic"));
}

sealed class RecordingHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler);
}

sealed class RecordingHandler : HttpMessageHandler
{
    public List<string> Paths { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Paths.Add(request.RequestUri?.AbsolutePath.TrimStart('/') ?? "");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

sealed class FailingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "ProofStorageChecks";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = "";
    public string WebRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

public class ProofReviewProxy : System.Reflection.DispatchProxy
{
    private readonly object gate = new();
    public Booking Booking { get; set; } = null!;
    public AppUser Admin { get; set; } = null!;
    public Payment Payment { get; set; } = null!;
    public byte[] ProofBytes { get; set; } = [];
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_Gate" => gate,
        "get_IsPersistent" => true,
        "get_Users" => new List<AppUser> { Admin },
        "get_Bookings" => new List<Booking> { Booking },
        "FindBooking" => (Guid)args![0]! == Booking.Id ? Booking : null,
        "get_Payments" => new List<Payment> { Payment },
        nameof(IPersistentStudioStore.ReviewPayments) => new List<Payment> { Payment },
        nameof(IPersistentStudioStore.ReadAsset) when (string?)args![0] == Payment.ProofKey => (ProofBytes, "image/png"),
        _ => throw new InvalidOperationException("Unexpected fake store operation: " + method?.Name)
    };
}
