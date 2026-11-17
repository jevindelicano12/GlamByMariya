using System.Text.RegularExpressions;
using GlamByMariya.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }

var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Supabase:Url"] = "https://independent-placeholder.supabase.co/",
        ["B2:Endpoint"] = "https://s3.us-west-004.backblazeb2.com/",
        ["Ignored:Url"] = "https://ignored.example.test"
    })
    .Build();

var origins = ContentSecurityPolicy.ImageOrigins(config);
Check(origins.Count == 2, "Only configured Supabase and B2 origins are included");
Check(origins.Contains("https://independent-placeholder.supabase.co"), "Supabase origin is normalized without a trailing slash");
Check(origins.Contains("https://s3.us-west-004.backblazeb2.com"), "Configured B2 endpoint origin is included");
Check(!origins.Contains("https://ignored.example.test"), "Unrelated configured URLs are not added to img-src");

var duplicateConfig = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Supabase:Url"] = "https://example.supabase.co",
        ["B2:Endpoint"] = "https://EXAMPLE.supabase.co/path"
    })
    .Build();
Check(ContentSecurityPolicy.ImageOrigins(duplicateConfig).Count == 1, "Duplicate origins are deduplicated case-insensitively");

var invalidConfig = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Supabase:Url"] = "not-a-url",
        ["B2:Endpoint"] = "/relative"
    })
    .Build();
Check(ContentSecurityPolicy.ImageOrigins(invalidConfig).Count == 0, "Relative and invalid configured origins are ignored");

var nonce1 = ContentSecurityPolicy.CreateNonce();
var nonce2 = ContentSecurityPolicy.CreateNonce();
Check(nonce1.Length == 22 && nonce1 != nonce2, "Nonces are 128-bit base64url values and unique");
Check(Regex.IsMatch(nonce1, "^[A-Za-z0-9_-]{22}$"), "Nonce uses only base64url-safe characters");

var policy = ContentSecurityPolicy.BuildReportOnly(nonce1, origins);
Check(policy.Contains("default-src 'self'"), "Policy defaults to same-origin");
Check(policy.Contains("frame-ancestors 'none'"), "Policy denies framing");
Check(policy.Contains($"script-src 'self' 'nonce-{nonce1}'"), "Inline scripts require the per-request nonce and same-origin scripts");
Check(policy.Contains($"script-src-elem 'self' 'nonce-{nonce1}'"), "External script elements are same-origin and nonce-bound");
Check(!policy.Contains("https://cdn.jsdelivr.net"), "No third-party script origin remains after self-hosting Zod");
Check(!policy.Contains("unsafe-eval"), "Policy does not allow unsafe-eval");
var scriptDirectives = policy.Split(';').Select(x => x.Trim())
    .Where(x => x.StartsWith("script-src ", StringComparison.Ordinal) || x.StartsWith("script-src-elem ", StringComparison.Ordinal))
    .ToList();
Check(scriptDirectives.Count == 2 && scriptDirectives.All(x => !x.Contains("'unsafe-inline'")), "Script directives do not allow unsafe-inline");
Check(policy.Contains("script-src-attr 'none'"), "Inline event handler attributes are denied");
Check(policy.Contains("style-src 'self' https://fonts.googleapis.com"), "Stylesheets can use self and Google Fonts");
Check(policy.Contains("style-src-attr 'none'"), "Inline style attributes are denied");
Check(!policy.Contains("style-src-attr 'unsafe-inline'"), "No inline style attribute exception remains");
Check(policy.Contains("font-src 'self' https://fonts.gstatic.com"), "Google Fonts files are allowed");
Check(policy.Contains("img-src 'self' https://independent-placeholder.supabase.co https://s3.us-west-004.backblazeb2.com"), "Configured image origins are allowed");
Check(policy.Contains("connect-src 'self'"), "Same-origin fetch requests are allowed");
Check(policy.Contains("form-action 'self'"), "Forms can only post to this application");
Check(policy.Contains("base-uri 'self'"), "Base URIs are restricted to same-origin");
Check(policy.Contains("object-src 'none'") && policy.Contains("frame-src 'none'"), "Objects and frames are denied");
Check(policy.Contains("worker-src 'none'") && policy.Contains("media-src 'none'") && policy.Contains("manifest-src 'none'"), "Unused workers, media and manifests are denied");
Check(!Regex.IsMatch(policy, @"(^|[\s;])\*($|[\s;])"), "Policy contains no unrestricted wildcard");
Console.WriteLine($"ALL CSP POLICY CHECKS PASSED ({checks} checks)");
