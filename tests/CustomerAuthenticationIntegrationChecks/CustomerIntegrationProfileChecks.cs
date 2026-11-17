using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GlamByMariya.Infrastructure.Integrations;
using GlamByMariya.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class CustomerIntegrationProfileChecks
{
    internal static async Task RunAsync()
    {
        var passed=0;
        void Check(bool value,string label)
        {if(!value)throw new InvalidOperationException("FAIL "+label);passed++;Console.WriteLine("PASS "+label);}
        var builder=WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
            EnvironmentName="V2CustomerAuthTest",ContentRootPath=Path.GetFullPath("Frontend/GlamByMariya.Web") });
        const string project="aaaaaaaaaaaaaaaaaaaa";
        var settings=new Dictionary<string,string?> {
            ["Deployment:Profile"]=CustomerAuthIntegrationProfile.Name,["Deployment:V2ProjectId"]=project,
            ["PublicSite:InformationOnly"]="false",["Email:DeliveryDisabled"]="true",
            ["Authentication:DurableSessionEnforcement"]="true",["Authentication:RestrictLegacyFallback"]="true",
            ["Authentication:IntegrationCustomerEmail"]="synthetic@example.test",
            ["Database:Provider"]="PostgreSql",["Database:Schema"]="public",["Database:ProjectId"]=project,
            ["Database:Host"]=$"db.{project}.supabase.co",["Database:Username"]="glam_v2_backend",
            ["Database:Password"]="synthetic-password-not-real",["Studio:TimeZone"]="Asia/Manila",
            ["urls"]="https://127.0.0.1:7144",["Supabase:Url"]=$"https://{project}.supabase.co",
            ["Supabase:AnonKey"]="sb_publishable_synthetic_not_a_real_key",
            ["DataProtection:KeyRingPath"]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GlamByMariyaV2","CustomerAuthKeys"),
            ["DataProtection:CertificatePfxBase64"]="synthetic-no-certificate",
            ["DataProtection:CertificatePassword"]="synthetic-password" };
        IConfiguration Config(Dictionary<string,string?> values)=>new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        bool Reject(Dictionary<string,string?> values)
        {
            try {V2DeploymentConfiguration.Validate(Config(values),builder.Environment);return false;}
            catch(InvalidOperationException){return true;}
        }
        Check(!Reject(settings),"isolated V2-only configuration validates without any network call");
        foreach(var (key,value) in new (string,string?)[] {
            ("Deployment:V2ProjectId","bbbbbbbbbbbbbbbbbbbb"),("Database:ProjectId","bbbbbbbbbbbbbbbbbbbb"),
            ("Supabase:Url","https://bbbbbbbbbbbbbbbbbbbb.supabase.co"),("Supabase:Url",$"https://{project}.supabase.co/extra"),
            ("Database:Username","postgres"),("Database:Username","service_role"),("Database:Port","6543"),
            ("Database:Host","v1.invalid"),("Database:Schema","makeup"),("Database:Provider","Demo"),
            ("Authentication:DurableSessionEnforcement","false"),("Authentication:RestrictLegacyFallback","false"),
            ("Authentication:IdentityLinkingEnabled","true"),("Authentication:IntegrationCustomerEmail",""),
            ("PublicSite:InformationOnly","true"),("Email:DeliveryDisabled","false"),
            ("Supabase:ServiceKey","synthetic-forbidden"),("SupabaseStorage:ServiceKey","synthetic-forbidden"),
            ("Email:BrevoApiKey","synthetic-forbidden"),("DemoGallery:Enabled","true"),
            ("B2:AppKey","synthetic-forbidden"),("FileStorage:Provider","Supabase"),
            ("urls","http://127.0.0.1:7144"),("urls","https://0.0.0.0:7144"),("urls","https://127.0.0.1:7144;https://localhost:7145"),
            ("PORT","10000"),("Kestrel:Endpoints:Other:Url","http://0.0.0.0:8000"),
            ("DataProtection:KeyRingPath",Path.GetFullPath("temp/forbidden-real-keys")),
            ("DataProtection:KeyRingPath","relative-keys"),("DataProtection:CertificatePfxBase64",null),
            ("Studio:TimeZone",null),("Supabase:AnonKey","sb_secret_synthetic-forbidden"),
        })
        {
            var changed=new Dictionary<string,string?>(settings){[key]=value};
            Check(Reject(changed),"profile rejects "+key+" mismatch/unsafe value");
        }
        foreach(var environment in new[]{"Production","Development"})
        {
            builder.Environment.EnvironmentName=environment;
            Check(Reject(settings),"test profile cannot activate in "+environment);
        }
        builder.Environment.EnvironmentName="V2CustomerAuthTest";
        string Anon(string role,string reference)=>"header."+Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new {role,@ref=reference}))).TrimEnd('=').Replace('+','-').Replace('/','_')+".synthetic";
        Check(!Reject(new(settings){["Supabase:AnonKey"]=Anon("anon",project)}),"matching legacy anon classification accepted");
        Check(Reject(new(settings){["Supabase:AnonKey"]=Anon("service_role",project)}),"elevated legacy JWT refused as public key");
        Check(Reject(new(settings){["Supabase:AnonKey"]=Anon("anon","bbbbbbbbbbbbbbbbbbbb")}),"other-project legacy anon refused");
        var cookie=new CookieAuthenticationOptions();
        CustomerAuthIntegrationProfile.ConfigureCookie(cookie);
        Check(cookie.Cookie.Name=="__Host-GlamV2.CustomerAuthTest" && cookie.Cookie.HttpOnly &&
            cookie.Cookie.SecurePolicy==CookieSecurePolicy.Always && cookie.Cookie.SameSite==SameSiteMode.Strict &&
            cookie.Cookie.Path=="/" && cookie.Cookie.Domain==null && !cookie.SlidingExpiration &&
            cookie.ExpireTimeSpan==TimeSpan.FromMinutes(30),"test cookies are host-only secure strict non-sliding and bounded");
        var context=new DefaultHttpContext();
        context.Request.Scheme="https";context.Request.Host=new HostString("127.0.0.1",7144);
        context.Connection.LocalIpAddress=IPAddress.Loopback;context.Connection.RemoteIpAddress=IPAddress.Loopback;
        Check(CustomerAuthIntegrationProfile.LocalHttpsRequest(context),"actual local HTTPS request accepted");
        context.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");
        context.Request.Headers["X-Forwarded-For"]="127.0.0.1";
        context.Request.Headers["X-Forwarded-Proto"]="https";
        Check(!CustomerAuthIntegrationProfile.LocalHttpsRequest(context),"forwarded headers cannot open isolated boundary");
        Check(!CustomerAuthIntegrationProfile.AllowedPath("/Bookings/Create","POST") &&
            !CustomerAuthIntegrationProfile.AllowedPath("/Account/AcceptInvite","POST") &&
            !CustomerAuthIntegrationProfile.AllowedPath("/css/site.css","POST") &&
            CustomerAuthIntegrationProfile.AllowedPath("/Account/Verify","POST"),"only auth mutations and read-only assets are exposed");
        var fake=new FakeCustomerSupabase();
        var provider=new SupabaseAuthService(fake,new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Supabase:Url"]="https://auth.example.test",["Supabase:AnonKey"]="sb_publishable_synthetic_not_a_real_key" }).Build(),
            NullLogger<SupabaseAuthService>.Instance);
        await provider.SignUpAsync("publishable@example.test","Synthetic123!");
        Check(!fake.SignupHadBearer,"publishable API key is not sent as a JWT bearer token");
        try {CustomerAuthIntegrationProfile.LoadCertificate(Config(settings));Check(false,"invalid PFX rejected");}
        catch(InvalidOperationException e) {Check(!e.Message.Contains("synthetic-password"),"invalid PFX diagnostics do not echo secret values");}
        using var rsa=RSA.Create(2048);
        var request=new CertificateRequest("CN=synthetic DP test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new("1.3.6.1.5.5.7.3.1")},true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30));
        using var loaded=CustomerAuthIntegrationProfile.LoadCertificate(Config(new(settings) {
            ["DataProtection:CertificatePfxBase64"]=Convert.ToBase64String(certificate.Export(X509ContentType.Pfx,"synthetic-password")) }));
        Check(loaded.HasPrivateKey,"private RSA certificate secret loads without a PFX file in source");
        await CustomerMvcChecks.SurfaceSmoke(certificate);
        Console.WriteLine($"{passed} Customer integration configuration/provider checks passed; synthetic settings only.");
    }
}
