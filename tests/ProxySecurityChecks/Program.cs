using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var root = Directory.GetCurrentDirectory();
var program = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Program.cs"));
var render = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "v2", "render.yaml"));
var admin = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Controllers", "AdminController.cs"));
var securityHeaders = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Middleware", "SecurityHeadersMiddleware.cs"));
var cookieAuthentication = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Security", "StudioCookieAuthentication.cs"));
var csp = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Middleware", "ContentSecurityPolicy.cs"));
var loginView = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Views", "Account", "Login.cshtml"));
var priceView = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Views", "Shared", "_BookingPriceDetails.cshtml"));
var createView = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "Views", "Bookings", "Create.cshtml"));
var siteCss = await File.ReadAllTextAsync(Path.Combine(root, "Frontend", "GlamByMariya.Web", "wwwroot", "css", "site.css"));
var zodVendorPath = Path.Combine(root, "Frontend", "GlamByMariya.Web", "wwwroot", "js", "vendor", "zod-3.23.8", "index.umd.min.js");
var zodBytes = await File.ReadAllBytesAsync(zodVendorPath);
var zodSha256 = Convert.ToHexString(SHA256.HashData(zodBytes));
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }

Check(!program.Contains("KnownIPNetworks.Clear()") && !program.Contains("KnownProxies.Clear()"), "Forwarded-header trust is not globally unrestricted");
Check(program.Contains("ForwardedHeaders:KnownProxies") && program.Contains("IPAddress.TryParse"), "Trusted proxy addresses are explicit configuration, not guessed infrastructure");
Check(cookieAuthentication.Contains("CookieSecurePolicy.Always") && cookieAuthentication.Contains("Cookie.HttpOnly = true"), "Production authentication cookies remain secure and HttpOnly");
Check(program.Contains("UseForwardedHeaders()") && program.Contains("MapGet(\"/healthz\""), "Forwarded headers and dependency-free health checks remain in the pipeline");
Check(admin.Contains("Request.Scheme") && admin.Contains("AcceptInvite"), "Invitation URLs continue using the request scheme");
Check(IPAddress.TryParse("127.0.0.1", out _), "Security test runtime can parse explicit proxy IP configuration");
Check(!render.Contains("ForwardedHeaders__KnownProxies"), "No unverified Render proxy IP was added to deployment configuration");
Check(securityHeaders.Contains("\"X-Content-Type-Options\"") && securityHeaders.Contains("\"nosniff\""), "Every response declares X-Content-Type-Options: nosniff");
Check(securityHeaders.Contains("\"Referrer-Policy\"") && securityHeaders.Contains("\"strict-origin-when-cross-origin\""), "Every response declares Referrer-Policy: strict-origin-when-cross-origin");
Check(securityHeaders.Contains("\"X-Frame-Options\"") && securityHeaders.Contains("\"DENY\""), "Every response declares X-Frame-Options: DENY");
Check(program.Contains("SuppressXFrameOptionsHeader = true"), "Antiforgery's SAMEORIGIN header is suppressed so DENY is the only frame policy");
Check(!securityHeaders.Contains("\"Content-Security-Policy\"") && !csp.Contains("\"Content-Security-Policy\""), "No enforcing Content-Security-Policy header is emitted");
Check(securityHeaders.Contains("ContentSecurityPolicy.ReportOnlyHeader") && csp.Contains("Content-Security-Policy-Report-Only"), "CSP is emitted in report-only mode for auditing");
Check(csp.Contains("frame-ancestors 'none'"), "Report-only CSP declares frame-ancestors 'none'");
Check(csp.Contains("'nonce-") && !csp.Contains("unsafe-eval"), "Report-only CSP nonces inline scripts and never permits unsafe-eval");
Check(!csp.Contains("https://cdn.jsdelivr.net") && !csp.Contains("ZodCdnOrigin") && csp.Contains("https://fonts.googleapis.com") && csp.Contains("https://fonts.gstatic.com"), "Report-only CSP has no third-party script origin and names Google Fonts explicitly");
Check(zodBytes.Length == 61033 && zodSha256 == "CB348CC9E2F4AB4D16A74A1433C957416376C11ADBABB41484D24D53C2E6ED09", "Vendored Zod bytes and SHA-256 match the verified artifact");
Check(loginView.Contains("~/js/vendor/zod-3.23.8/index.umd.min.js") && loginView.Contains("asp-append-version=\"true\"") && !loginView.Contains("cdn.jsdelivr.net"), "Zod is loaded locally with cache-busting and no jsDelivr reference");
Check(csp.Contains("Supabase:Url") && csp.Contains("B2:Endpoint"), "Image origins come only from configured Supabase and B2 settings, never guessed domains");
Check(!loginView.Contains("style=") && !priceView.Contains("style=") && !createView.Contains("style="), "Audited inline style attributes were removed");
Check(loginView.Contains("eye-closed is-hidden"), "Password eye icon is hidden by a CSS class");
Check(priceView.Contains("booking-price-details") && createView.Contains("booking-type-spaced"), "Booking spacing uses CSS classes");
Check(siteCss.Contains(".password-toggle .eye-closed.is-hidden{display:none}") && siteCss.Contains(".booking-price-details{margin-bottom:24px}") && siteCss.Contains(".booking-type-spaced{margin-top:20px}"), "Replacement CSS preserves the original styles");
Check(csp.Contains("style-src-attr 'none'"), "Report-only CSP denies inline style attributes");
var securityAt = program.IndexOf("UseMiddleware<SecurityHeadersMiddleware>()", StringComparison.Ordinal);
var staticAt = program.IndexOf("UseStaticFiles()", StringComparison.Ordinal);
Check(securityAt > 0 && staticAt > securityAt, "Security headers run before static files");
var developmentAt = program.IndexOf("if (!app.Environment.IsDevelopment())", StringComparison.Ordinal);
var hstsAt = program.IndexOf("app.UseHsts();", StringComparison.Ordinal);
var forwardedAt = program.IndexOf("app.UseForwardedHeaders();", StringComparison.Ordinal);
Check(developmentAt >= 0 && hstsAt > developmentAt && forwardedAt >= 0 && forwardedAt < developmentAt,
    "Trusted forwarded headers run before production-only HSTS and exception handling");
var httpsRedirectAt = program.IndexOf("app.UseHttpsRedirection(", StringComparison.Ordinal);
Check(httpsRedirectAt < 0 || forwardedAt < httpsRedirectAt, "Any HTTPS redirection runs after forwarded scheme processing");
Check(program.Contains("options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;"),
    "Only forwarded client IP and scheme are enabled, not forwarded Host");

// Exercise the real ASP.NET Core middleware in memory. Remote IPs here are
// simulated peers, never TCP targets; there is no database, network or auth state.
// Keep the options aligned with the production configuration checked above.
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Parse("192.0.2.10"));
});
services.AddHsts(_ => { });
services.AddHttpsRedirection(options => options.HttpsPort = 443);
using var provider = services.BuildServiceProvider();
var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
Check(options.ForwardLimit == 1, "Default one-hop forwarding limit is preserved");
Check(options.KnownIPNetworks.Count > 0 && options.KnownProxies.Contains(IPAddress.IPv6Loopback)
    && options.KnownProxies.Contains(IPAddress.Parse("192.0.2.10")), "Default loopback trust coexists with an explicit proxy IP");

async Task<DefaultHttpContext> Probe(string remoteIp, string? forwardedProto = "https", string? forwardedFor = "203.0.113.25",
    bool production = true, bool redirect = false, string scheme = "http")
{
    var context = new DefaultHttpContext { RequestServices = provider };
    context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
    context.Request.Scheme = scheme;
    context.Request.Host = new HostString("audit.example.invalid");
    context.Request.Path = "/account";
    context.Request.QueryString = new QueryString("?returnUrl=%2FBookings");
    if (forwardedProto != null) context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
    if (forwardedFor != null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
    context.Request.Headers["X-Forwarded-Host"] = "attacker.example.invalid";
    var pipeline = new ApplicationBuilder(provider);
    pipeline.UseForwardedHeaders();
    if (production) pipeline.UseHsts();
    if (redirect) pipeline.UseHttpsRedirection();
    pipeline.Run(_ => Task.CompletedTask);
    await pipeline.Build()(context);
    return context;
}

foreach (var peer in new[] { "127.0.0.1", "::1", "::ffff:127.0.0.1", "192.0.2.10" })
{
    var trusted = await Probe(peer);
    Check(trusted.Request.IsHttps, $"Trusted peer {peer}: forwarded HTTPS accepted");
    Check(trusted.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("203.0.113.25")), $"Trusted peer {peer}: forwarded client IP accepted");
    Check(trusted.Response.Headers.StrictTransportSecurity.ToString().StartsWith("max-age="), $"Trusted peer {peer}: Production HSTS emitted");
    Check(trusted.Request.Host.Value == "audit.example.invalid", $"Trusted peer {peer}: forwarded Host ignored");
}
foreach (var peer in new[] { "198.51.100.10", "2001:db8::10" })
{
    var untrusted = await Probe(peer, forwardedFor: "127.0.0.1");
    Check(!untrusted.Request.IsHttps, $"Untrusted peer {peer}: spoofed HTTPS ignored");
    Check(untrusted.Connection.RemoteIpAddress!.Equals(IPAddress.Parse(peer)), $"Untrusted peer {peer}: spoofed client IP ignored");
    Check(!untrusted.Response.Headers.ContainsKey("Strict-Transport-Security"), $"Untrusted peer {peer}: spoofed HTTPS cannot enable HSTS");
}
var chain = await Probe("127.0.0.1", "https, http", "127.0.0.1, 198.51.100.20");
Check(!chain.Request.IsHttps && chain.Connection.RemoteIpAddress!.Equals(IPAddress.Parse("198.51.100.20")),
    "Only the nearest hop is consumed; an extra claimed loopback/HTTPS hop is not trusted");
var directTls = await Probe("198.51.100.10", null, null, scheme: "https");
Check(directTls.Request.IsHttps && directTls.Response.Headers.ContainsKey("Strict-Transport-Security"), "Direct TLS receives Production HSTS without forwarded headers");
var development = await Probe("127.0.0.1", production: false);
Check(development.Request.IsHttps && !development.Response.Headers.ContainsKey("Strict-Transport-Security"), "Development honors trusted scheme without emitting HSTS");

// The application currently has no UseHttpsRedirection. Exercise the framework
// redirect middleware separately to guard ordering if it is added in the future;
// do not introduce a new application redirect policy as part of this fix.
var alreadyHttps = await Probe("192.0.2.10", redirect: true);
Check(alreadyHttps.Response.StatusCode == 200 && !alreadyHttps.Response.Headers.ContainsKey("Location"), "Trusted forwarded HTTPS does not enter an HTTPS redirect loop");
var toHttps = await Probe("192.0.2.10", "http", redirect: true);
Check(toHttps.Response.StatusCode == 307 && toHttps.Response.Headers.Location == "https://audit.example.invalid/account?returnUrl=%2FBookings", "Forwarded HTTP redirects once to the correct HTTPS host, path and query");
var spoofHttps = await Probe("198.51.100.10", redirect: true);
Check(spoofHttps.Response.StatusCode == 307 && !spoofHttps.Request.IsHttps, "Untrusted HTTPS header cannot suppress an HTTPS redirect");
var spoofHttp = await Probe("198.51.100.10", "http", redirect: true, scheme: "https");
Check(spoofHttp.Response.StatusCode == 200 && spoofHttp.Request.IsHttps, "Untrusted HTTP header cannot downgrade direct TLS or cause a loop");
Console.WriteLine($"All proxy security checks passed ({checks} checks).");
