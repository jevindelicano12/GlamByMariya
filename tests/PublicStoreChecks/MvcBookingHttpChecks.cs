using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Integrations;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Web.Security;
using ImageMagick;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

// Synthetic loopback host only. Uses the real controllers and guarded PostgreSQL source.
internal static class MvcBookingHttpChecks
{
    internal static async Task RunAsync(NpgsqlDataSource source, string root, Guid artist, AppUser customer, AppUser other, AppUser admin)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {
            Args = [], EnvironmentName = "Development", ContentRootPath = root,
            ApplicationName = typeof(BookingsController).Assembly.GetName().Name
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> { ["Database:ArtistId"] = artist.ToString() });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddStudioCookieAuthentication(true);
        builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme, o => {
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode=401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode=403; return Task.CompletedTask; };
        });
        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(source);
        builder.Services.AddDbContext<PublicMakeupDbContext>(o => o.UseNpgsql(source));
        builder.Services.AddScoped<PublicStudioStore>();
        builder.Services.AddScoped<IStudioStore>(sp => sp.GetRequiredService<PublicStudioStore>());
        builder.Services.AddScoped<IServiceRepository>(sp => sp.GetRequiredService<IStudioStore>());
        builder.Services.AddScoped<IAddOnRepository>(sp => sp.GetRequiredService<IStudioStore>());
        builder.Services.AddScoped<IBookingRepository>(sp => sp.GetRequiredService<IStudioStore>());
        builder.Services.AddScoped<IPaymentRepository>(sp => sp.GetRequiredService<IStudioStore>());
        builder.Services.AddScoped<BookingService>();
        builder.Services.AddScoped<AccountSettingsService>();
        builder.Services.AddScoped<IAppFileStore>(sp => new LocalAppFileStore(sp.GetRequiredService<IWebHostEnvironment>()));
        builder.Services.AddScoped(sp => new PaymentQrService(root, sp.GetRequiredService<PublicStudioStore>()));
        builder.Services.AddScoped(sp => new CatalogImageService(root, sp.GetRequiredService<PublicStudioStore>()));
        builder.Services.AddControllersWithViews(o => o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
            .AddApplicationPart(typeof(BookingsController).Assembly);
        builder.Services.AddSingleton<IControllerActivator, BookingControllers>();
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapControllerRoute("mvc", "{controller=Home}/{action=Index}/{id?}");
        var prefix = "/__synthetic_" + Guid.NewGuid().ToString("N");
        app.MapGet(prefix + "/csrf", (HttpContext c, IAntiforgery a) => a.GetAndStoreTokens(c).RequestToken!);
        var users = new Dictionary<string,AppUser> { ["customer"]=customer, ["other"]=other, ["admin"]=admin };
        app.MapPost(prefix + "/login/{role}", async (string role, HttpContext c, IAntiforgery a) => {
            try { await a.ValidateRequestAsync(c); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            if (!users.TryGetValue(role, out var u)) return Results.Unauthorized();
            var claims = new[] { new Claim(ClaimTypes.NameIdentifier,u.Id.ToString()), new Claim(ClaimTypes.Email,u.Email),
                new Claim(ClaimTypes.Role,u.Role), new Claim("account_version",AccountSession.Version(u.PasswordHash)) };
            await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims,CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok();
        });
        await app.StartAsync();
        var count = 0;
        void Check(bool ok, string label) { if (!ok) throw new Exception("MVC booking FAIL: " + label); count++; Console.WriteLine("HTTP PASS: " + label); }
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            HttpClient Browser() => new(new HttpClientHandler { CookieContainer=new CookieContainer(), AllowAutoRedirect=false }) { BaseAddress=new Uri(address) };
            using var c = Browser(); using var o = Browser(); using var a = Browser();
            async Task<string> Csrf(HttpClient browser) => await browser.GetStringAsync(prefix + "/csrf");
            async Task<HttpResponseMessage> Post(HttpClient browser, string path, HttpContent content, bool csrf=true) {
                using var request = new HttpRequestMessage(HttpMethod.Post,path) { Content=content };
                if (csrf) request.Headers.Add("X-CSRF-TOKEN",await Csrf(browser));
                return await browser.SendAsync(request);
            }
            foreach (var (browser,role) in new[] { (c,"customer"),(o,"other"),(a,"admin") })
                Check((await Post(browser,prefix+"/login/"+role,new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.OK, role+" synthetic cookie issued");

            using var scope = app.Services.CreateScope();
            var repository=scope.ServiceProvider.GetRequiredService<PublicStudioStore>();
            var service=repository.Services.Single(x=>x.Name=="Soft Glam");
            var date=repository.Now.Date.AddDays(14).AddHours(10);
            var start=date.ToString("yyyy-MM-ddTHH:mm");
            using var quote=JsonDocument.Parse(await c.GetStringAsync($"/Bookings/Quote?serviceId={service.Id}&people=1&package=auto"));
            var total=quote.RootElement.GetProperty("finalTotal").GetDecimal();
            var minimum=quote.RootElement.GetProperty("requiredDownPayment").GetDecimal();
            Check(total>0 && minimum>0 && quote.RootElement.GetProperty("travelFee").GetDecimal()==0,"real quote excludes travel fees");
            var availability=await c.GetAsync($"/Bookings/Availability?month={date:yyyy-MM}&serviceId={service.Id}&people=1&package=auto");
            Check(availability.StatusCode==HttpStatusCode.OK,"Customer reads real persistent availability");
            using var slot=JsonDocument.Parse(await c.GetStringAsync($"/Bookings/ValidateSlot?start={Uri.EscapeDataString(start)}&serviceId={service.Id}&people=1&package=auto"));
            Check(slot.RootElement.GetProperty("available").GetBoolean(),"selected service slot is initially available");
            byte[] Image(MagickColor color) { using var img=new MagickImage(color,8,8); img.Format=MagickFormat.Png; return img.ToByteArray(); }
            var original=Image(MagickColors.White); var revised=Image(MagickColors.Black);
            MultipartFormDataContent BookingForm(string reference, decimal expected, string location="24 Synthetic Road, Barangay Test", string? chosenStart=null, decimal? claimed=null, decimal? chosenMinimum=null) {
                var form=new MultipartFormDataContent();
                foreach(var pair in new Dictionary<string,string> {
                    ["serviceId"]=service.Id.ToString(), ["start"]=chosenStart ?? start, ["address"]=location, ["area"]="Quezon City",
                    ["deposit"]=(claimed ?? chosenMinimum ?? minimum).ToString(System.Globalization.CultureInfo.InvariantCulture), ["method"]="GCash", ["reference"]=reference,
                    ["agreed"]="true", ["travelAcknowledged"]="true", ["people"]="1", ["package"]="auto",
                    ["expectedTotal"]=expected.ToString(System.Globalization.CultureInfo.InvariantCulture), ["expectedMinimum"]=(chosenMinimum ?? minimum).ToString(System.Globalization.CultureInfo.InvariantCulture)
                }) form.Add(new StringContent(pair.Value),pair.Key);
                form.Add(new ByteArrayContent(original),"proof","synthetic.png"); return form;
            }
            int Count() => repository.Bookings.Count(b=>b.CustomerId==customer.Id);
            var baseline=Count();
            var paymentBaseline=repository.Payments.Count;
            Check((await Post(c,"/Bookings/Create",BookingForm("HTTP-NO-CSRF",total),false)).StatusCode==HttpStatusCode.BadRequest && Count()==baseline,"submission requires antiforgery without database write");
            await Post(c,"/Bookings/Create",BookingForm("HTTP-STALE",total+1));
            Check(Count()==baseline && repository.Payments.Count==paymentBaseline,"stale reviewed price rolls back without booking or payment");
            await Post(c,"/Bookings/Create",BookingForm("HTTP-ADDRESS",total,"short"));
            Check(Count()==baseline && repository.Payments.Count==paymentBaseline,"invalid home-service address is rejected without booking or payment");
            var submit=await Post(c,"/Bookings/Create",BookingForm("HTTP-INITIAL",total));
            var booking=repository.Bookings.Single(b=>b.CustomerId==customer.Id);
            Check(submit.StatusCode==HttpStatusCode.Redirect && booking.Status=="Pending Payment" && booking.Address=="24 Synthetic Road, Barangay Test","Customer HTTP submission persists pending booking and address");
            var payments=repository.ReviewPayments(booking.Id);
            Check(payments.Count==1 && !payments[0].Verified && payments[0].Reference=="HTTP-INITIAL","PostgreSQL persists the unverified payment");
            var initialCustomer=await c.GetStringAsync("/Bookings/Details/"+booking.Id);
            var initialAdmin=await a.GetStringAsync("/Admin/Details/"+booking.Id);
            Check(payments[0].ReviewStatus=="Pending Verification" &&
                !initialCustomer.Contains("Payment correction requested") && !initialAdmin.Contains("Payment correction requested"),
                "initial claim remains pending without a correction-requested badge");
            Check(!(await a.GetStringAsync("/Admin")).Contains("Correction requested") &&
                !(await a.GetStringAsync("/Admin/Appointments?mode=list")).Contains("Correction requested"),
                "Admin lists do not invent an initial correction status");
            using (var occupied=JsonDocument.Parse(await c.GetStringAsync($"/Bookings/ValidateSlot?start={Uri.EscapeDataString(start)}&serviceId={service.Id}&people=1&package=auto")))
                Check(!occupied.RootElement.GetProperty("available").GetBoolean(),"Customer sees their pending reservation as unavailable");
            var otherAvailability=await o.GetStringAsync($"/Bookings/Availability?month={date:yyyy-MM}&serviceId={service.Id}&people=1&package=auto");
            using (var otherSlot=JsonDocument.Parse(await o.GetStringAsync($"/Bookings/ValidateSlot?start={Uri.EscapeDataString(start)}&serviceId={service.Id}&people=1&package=auto")))
                Check(!otherSlot.RootElement.GetProperty("available").GetBoolean() &&
                    !otherAvailability.Contains("HTTP-INITIAL") && !otherAvailability.Contains("24 Synthetic Road") &&
                    !otherAvailability.Contains(customer.Id.ToString()),
                    "another Customer sees the pending slot unavailable without booking details");
            var end=date.AddMinutes(quote.RootElement.GetProperty("durationMinutes").GetInt32()+repository.Settings.BufferMinutes);
            async Task<bool> Available(DateTime when) {
                using var result=JsonDocument.Parse(await o.GetStringAsync($"/Bookings/ValidateSlot?start={Uri.EscapeDataString(when.ToString("yyyy-MM-ddTHH:mm"))}&serviceId={service.Id}&people=1&package=auto"));
                return result.RootElement.GetProperty("available").GetBoolean();
            }
            Check(!await Available(end.AddMinutes(-1)) && await Available(end),
                "service duration and preparation buffer block until the exact end boundary");
            await Post(a,"/Admin/Action",new FormUrlEncodedContent(new Dictionary<string,string> {
                ["id"]=booking.Id.ToString(), ["operation"]="proof", ["reason"]="Please correct the proof" }));
            Check(repository.Bookings.Single(b=>b.Id==booking.Id).Status=="Pending Payment" &&
                repository.ReviewPayments(booking.Id).Single().Reference=="HTTP-INITIAL",
                "payment correction request keeps the reservation pending");
            var correctionCustomer=await c.GetStringAsync("/Bookings/Details/"+booking.Id);
            var correctionAdmin=await a.GetStringAsync("/Admin/Details/"+booking.Id);
            Check(repository.ReviewPayments(booking.Id).Single().ReviewStatus=="Correction Requested" &&
                repository.Payments.Single(p=>p.BookingId==booking.Id).ReviewStatus=="Correction Requested" &&
                correctionCustomer.Contains("Payment correction requested") && correctionAdmin.Contains("Payment correction requested") &&
                correctionCustomer.Contains("aria-label=\"Payment review status\"") && correctionAdmin.Contains("aria-label=\"Payment review status\""),
                "persisted correction request appears to its Customer and authorized Admin");
            Check((await c.GetStringAsync("/Bookings")).Contains("Payment: Correction requested"),
                "Customer appointment list uses the authoritative correction state");
            var adminOverview=await a.GetStringAsync("/Admin");
            var adminList=await a.GetStringAsync("/Admin/Appointments?mode=list");
            Check(adminOverview.Contains("Correction requested") && adminList.Contains("Correction requested") &&
                !adminOverview.Contains("HTTP-INITIAL") && !adminList.Contains("HTTP-INITIAL"),
                "Admin overview and list show correction without private payment reference");
            Check((await c.GetAsync("/Admin/Appointments?mode=list")).StatusCode==HttpStatusCode.Forbidden,
                "Customer cannot access the Admin correction list");
            await Post(o,"/Bookings/Create",BookingForm("HTTP-CORRECTION-OVERLAP",total));
            Check(repository.Bookings.Count(b=>b.Start==date)==1,"another Customer cannot reserve a correction-requested slot");
            await Post(c,"/Bookings/Create",BookingForm("HTTP-INITIAL",total));
            Check(Count()==baseline+1 && repository.ReviewPayments(booking.Id).Count==1,"duplicate reference does not create a second booking");
            Check((await o.GetAsync("/Bookings/Details/"+booking.Id)).StatusCode==HttpStatusCode.NotFound,"another Customer cannot read the booking");
            Check((await c.GetAsync("/Admin/Details/"+booking.Id)).StatusCode==HttpStatusCode.Forbidden,"Customer cannot access Admin review");
            var detail=await a.GetAsync("/Admin/Details/"+booking.Id);
            var html=await detail.Content.ReadAsStringAsync();
            Check(detail.StatusCode==HttpStatusCode.OK && html.Contains("HTTP-INITIAL"),"Admin MVC details displays the payment reference");
            string Hidden(string name) { var m=Regex.Match(html,$"name=\"{name}\" value=\"([^\"]+)\""); return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : throw new Exception("Missing review field "+name); }
            var paymentId=Hidden("paymentId"); var revision=Hidden("reviewRevision"); var version=Hidden("reviewVersion");
            var proofUrl=$"/Admin/Proof/{paymentId}?bookingId={booking.Id}&reviewRevision={revision}&reviewVersion={version}";
            Check((await a.GetByteArrayAsync(proofUrl)).SequenceEqual(PaymentProofImageValidator.Normalize(original).Bytes),"Admin receives the exact reviewed proof");
            FormUrlEncodedContent VerifyForm(string rev,string ver) => new(new Dictionary<string,string> {
                ["id"]=booking.Id.ToString(), ["operation"]="verify", ["paymentId"]=paymentId,
                ["reviewRevision"]=rev, ["reviewVersion"]=ver, ["reviewed"]="true" });
            Check((await Post(c,"/Admin/Action",VerifyForm(revision,version))).StatusCode==HttpStatusCode.Forbidden &&
                (await Post(a,"/Admin/Action",VerifyForm(revision,version),false)).StatusCode==HttpStatusCode.BadRequest &&
                !repository.ReviewPayments(booking.Id).Single().Verified,"unauthorized and CSRF-free Admin actions cannot verify");
            var correction=new MultipartFormDataContent { { new StringContent(booking.Id.ToString()),"id" }, { new StringContent("HTTP-CORRECTED"),"reference" }, { new ByteArrayContent(revised),"proof","revised.png" } };
            Check((await Post(c,"/Bookings/CorrectProof",correction)).StatusCode==HttpStatusCode.Redirect,"Customer submits proof correction through MVC");
            Check(repository.ReviewPayments(booking.Id).Single().ReviewStatus=="Pending Verification" &&
                !(await c.GetStringAsync("/Bookings/Details/"+booking.Id)).Contains("Payment correction requested") &&
                !(await a.GetStringAsync("/Admin/Details/"+booking.Id)).Contains("Payment correction requested"),
                "corrected claim returns to generic pending verification without an invented distinction");
            Check(!(await a.GetStringAsync("/Admin")).Contains("Correction requested") &&
                !(await a.GetStringAsync("/Admin/Appointments?mode=list")).Contains("Correction requested"),
                "Admin lists return to generic pending status after correction");
            Check((await a.GetAsync(proofUrl)).StatusCode==HttpStatusCode.Conflict,"old proof link fails after correction");
            await Post(a,"/Admin/Action",VerifyForm(revision,version));
            Check(repository.Bookings.Single(b=>b.Id==booking.Id).Status=="Pending Payment" && !repository.ReviewPayments(booking.Id).Single().Verified,"stale Admin review cannot verify corrected payment");
            html=await (await a.GetAsync("/Admin/Details/"+booking.Id)).Content.ReadAsStringAsync();
            revision=Hidden("reviewRevision"); version=Hidden("reviewVersion");
            proofUrl=$"/Admin/Proof/{paymentId}?bookingId={booking.Id}&reviewRevision={revision}&reviewVersion={version}";
            Check((await a.GetByteArrayAsync(proofUrl)).SequenceEqual(PaymentProofImageValidator.Normalize(revised).Bytes),"Admin reloads the corrected proof bytes");
            Check((await Post(a,"/Admin/Action",VerifyForm(revision,version))).StatusCode==HttpStatusCode.Redirect,"Admin submits verification through MVC");
            var confirmed=repository.Bookings.Single(b=>b.Id==booking.Id);
            var verified=repository.ReviewPayments(booking.Id).Single();
            Check(confirmed.Status=="Confirmed" && verified.Verified && verified.Reference=="HTTP-CORRECTED","verified payment and confirmed booking persist together");
            Check(!await Available(date),"another Customer sees the confirmed slot unavailable");
            await Post(a,"/Admin/Action",VerifyForm(revision,version));
            Check(repository.ReviewPayments(booking.Id).Single().PersistentReviewRevision==verified.PersistentReviewRevision,"duplicate verification creates no further payment revision");
            var customerDetails=await c.GetAsync("/Bookings/Details/"+booking.Id);
            var adminDetails=await a.GetAsync("/Admin/Details/"+booking.Id);
            var customerHtml=await customerDetails.Content.ReadAsStringAsync();
            var adminHtml=await adminDetails.Content.ReadAsStringAsync();
            Check(customerDetails.StatusCode==HttpStatusCode.OK && adminDetails.StatusCode==HttpStatusCode.OK &&
                customerHtml.Contains("Confirmed") && customerHtml.Contains("HTTP-CORRECTED") && customerHtml.Contains("Verified") &&
                adminHtml.Contains("Confirmed") && adminHtml.Contains("HTTP-CORRECTED") && adminHtml.Contains("Verified"),
                "Customer and Admin pages display the same confirmed booking and verified payment");
            using var c2=Browser(); using var a2=Browser();
            Check((await Post(c2,prefix+"/login/customer",new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.OK &&
                (await Post(a2,prefix+"/login/admin",new FormUrlEncodedContent([]))).StatusCode==HttpStatusCode.OK,"independent sessions established for concurrent HTTP test");
            var next=date.AddDays(7).ToString("yyyy-MM-ddTHH:mm");
            await Task.WhenAll(
                Post(c,"/Bookings/Create",BookingForm("HTTP-RACE-A",total,chosenStart:next)),
                Post(c2,"/Bookings/Create",BookingForm("HTTP-RACE-B",total,chosenStart:next)));
            var race=repository.Bookings.Where(b=>b.CustomerId==customer.Id && b.Id!=booking.Id && b.Start==date.AddDays(7)).ToArray();
            Check(race.Length==1,"concurrent overlapping requests create exactly one pending reservation");
            async Task<HttpResponseMessage> VerifyRace(HttpClient browser, Booking candidate) {
                var response=await browser.GetAsync("/Admin/Details/"+candidate.Id);
                if (response.StatusCode!=HttpStatusCode.OK) throw new Exception("Concurrent review page unavailable.");
                var page=await response.Content.ReadAsStringAsync();
                string Field(string name) { var match=Regex.Match(page,$"name=\"{name}\" value=\"([^\"]+)\""); return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : throw new Exception("Missing concurrent review field "+name); }
                return await Post(browser,"/Admin/Action",new FormUrlEncodedContent(new Dictionary<string,string> {
                    ["id"]=candidate.Id.ToString(), ["operation"]="verify", ["paymentId"]=Field("paymentId"),
                    ["reviewRevision"]=Field("reviewRevision"), ["reviewVersion"]=Field("reviewVersion"), ["reviewed"]="true" }));
            }
            if (race.Length==2) await Task.WhenAll(VerifyRace(a,race[0]),VerifyRace(a2,race[1]));
            else await VerifyRace(a,race[0]);
            var raceStates=repository.Bookings.Where(b=>race.Select(r=>r.Id).Contains(b.Id)).ToArray();
            Check(raceStates.Count(b=>b.Status=="Confirmed")==1 &&
                raceStates.Sum(b=>repository.ReviewPayments(b.Id).Count(p=>p.Verified))==1,
                "concurrent overlapping HTTP requests produce at most one confirmed and verified booking");
            Console.WriteLine($"HTTP race: {race.Length} pending candidate(s), {raceStates.Count(b=>b.Status=="Confirmed")} confirmed.");
            var rejectedStart=date.AddDays(14);
            await Post(c,"/Bookings/Create",BookingForm("HTTP-REJECT",total,chosenStart:rejectedStart.ToString("yyyy-MM-ddTHH:mm"),claimed:(minimum+total)/2));
            var rejected=repository.Bookings.Single(b=>b.Start==rejectedStart);
            Check(repository.ReviewPayments(rejected.Id).Single().Amount==(minimum+total)/2,"higher partial payment is persisted unverified");
            await Post(a,"/Admin/Action",new FormUrlEncodedContent(new Dictionary<string,string> {
                ["id"]=rejected.Id.ToString(), ["operation"]="reject", ["reason"]="Synthetic rejection" }));
            Check(repository.Bookings.Single(b=>b.Id==rejected.Id).Status=="Rejected","Admin rejection is persisted");
            Check(await Available(rejectedStart),"another Customer sees a rejected slot reopen");
            await Post(c,"/Bookings/Create",BookingForm("HTTP-FULL",total,chosenStart:rejectedStart.ToString("yyyy-MM-ddTHH:mm"),claimed:total));
            var replacement=repository.Bookings.Single(b=>b.Start==rejectedStart && b.Id!=rejected.Id);
            Check(repository.ReviewPayments(replacement.Id).Single().Amount==total &&
                !repository.ReviewPayments(replacement.Id).Single().Verified,
                "rejection releases the slot and a full payment remains unverified");
            await VerifyRace(a,replacement);
            Check(repository.Bookings.Single(b=>b.Id==replacement.Id).Status=="Confirmed" &&
                repository.ReviewPayments(replacement.Id).Single().Verified,
                "authorized review verifies full payment and confirms the reservation");
            var partialStart=date.AddDays(21);
            await Post(c,"/Bookings/Create",BookingForm("HTTP-PARTIAL-VERIFY",total,chosenStart:partialStart.ToString("yyyy-MM-ddTHH:mm"),claimed:(minimum+total)/2));
            var partial=repository.Bookings.Single(b=>b.Start==partialStart);
            await VerifyRace(a,partial);
            Check(repository.Bookings.Single(b=>b.Id==partial.Id).Status=="Confirmed" &&
                repository.ReviewPayments(partial.Id).Single().Verified,
                "authorized review verifies higher partial payment and confirms the reservation");
            var invalidStart=date.AddDays(28);
            await Post(c,"/Bookings/Create",BookingForm("HTTP-TOO-LOW",total,chosenStart:invalidStart.ToString("yyyy-MM-ddTHH:mm"),claimed:minimum-0.01m));
            await Post(c,"/Bookings/Create",BookingForm("HTTP-TOO-HIGH",total,chosenStart:invalidStart.ToString("yyyy-MM-ddTHH:mm"),claimed:total+0.01m));
            await Post(c,"/Bookings/Create",BookingForm("HTTP-TOO-PRECISE",total,chosenStart:invalidStart.ToString("yyyy-MM-ddTHH:mm"),claimed:minimum+0.001m));
            Check(!repository.Bookings.Any(b=>b.Start==invalidStart),"amounts outside the allowed payment range are rejected");
            var addon=repository.AddOns.First(a=>a.Active && !a.Archived && a.Minutes>0);
            using var addonQuote=JsonDocument.Parse(await c.GetStringAsync($"/Bookings/Quote?serviceId={service.Id}&addOnIds={addon.Id}&people=1&package=auto"));
            var addonStart=date.AddDays(35);
            var addonForm=BookingForm("HTTP-ADDON",addonQuote.RootElement.GetProperty("finalTotal").GetDecimal(),
                chosenStart:addonStart.ToString("yyyy-MM-ddTHH:mm"),
                chosenMinimum:addonQuote.RootElement.GetProperty("requiredDownPayment").GetDecimal());
            addonForm.Add(new StringContent(addon.Id.ToString()),"addOnIds");
            await Post(c,"/Bookings/Create",addonForm);
            var addonEnd=addonStart.AddMinutes(addonQuote.RootElement.GetProperty("durationMinutes").GetInt32()+repository.Settings.BufferMinutes);
            Check(repository.Bookings.Any(b=>b.Start==addonStart) && !await Available(addonEnd.AddMinutes(-1)) && await Available(addonEnd),
                "add-on duration and preparation buffer are included once at the slot boundary");
            Console.WriteLine($"All {count} PostgreSQL-backed MVC HTTP checks passed.");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
    private sealed class DenyHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("External provider access is forbidden in this fixture.");
    }
    private sealed class BookingControllers : IControllerActivator
    {
        public object Create(ControllerContext c)
        {
            var sp=c.HttpContext.RequestServices;
            if (c.ActionDescriptor.ControllerTypeInfo.AsType()==typeof(AdminController))
                return new AdminController(sp.GetRequiredService<IStudioStore>(),sp.GetRequiredService<BookingService>(),
                    sp.GetRequiredService<PaymentQrService>(),sp.GetRequiredService<CatalogImageService>(),null!,null!);
            if (c.ActionDescriptor.ControllerTypeInfo.AsType()==typeof(BookingsController))
                return ActivatorUtilities.CreateInstance<BookingsController>(sp);
            throw new InvalidOperationException("Only booking test controllers are available.");
        }
        public void Release(ControllerContext c, object controller) { }
    }
}
