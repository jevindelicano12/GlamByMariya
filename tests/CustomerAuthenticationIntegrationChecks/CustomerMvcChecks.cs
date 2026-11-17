using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Integrations;
using GlamByMariya.Infrastructure.Services;
using GlamByMariya.Web.Controllers;
using GlamByMariya.Web.Middleware;
using GlamByMariya.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

internal static class CustomerMvcChecks
{
    internal static async Task RunAsync(NpgsqlDataSource admin,NpgsqlDataSource backend)
    {
        using var rsa=RSA.Create(2048);
        var request=new CertificateRequest("CN=V2 synthetic auth tests",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        var san=new SubjectAlternativeNameBuilder();san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection {new("1.3.6.1.5.5.7.3.1")},true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        using var generated=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(30));
        // Windows Schannel requires a temporary OS-backed private key; disposal
        // removes it. No certificate store/trust changes or PFX file are used.
        using var certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
        var keys=Path.GetFullPath(Path.Combine("temp","v2-auth-mvc-synthetic-keys-"+Guid.NewGuid().ToString("N")));
        var fake=new FakeCustomerSupabase();
        var app=BuildHost(backend,fake,keys,certificate);
        await app.StartAsync();
        var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var passed=0;
        void Check(bool condition,string label)
        {if(!condition)throw new InvalidOperationException("FAIL "+label);passed++;Console.WriteLine("PASS "+label);}
        Client New()=>new(address,certificate);
        await using(var scope=app.Services.CreateAsyncScope())
            Check(await scope.ServiceProvider.GetRequiredService<PersistentCustomerAuthentication>().Ready(default),
                "application startup readiness uses 0004 directly without MVC views/settings/key tables");
        using var surviving=New();using var pending=New();
        static Dictionary<string,string> Registration(string email)=>new() {
            ["firstName"]="Synthetic",["lastName"]="Customer",["email"]=email,["birthday"]="2001-01-01",
            ["suffix"]="Jr.",["gender"]="Others",["address"]="Synthetic test address",["password"]="Synthetic123!",
            ["role"]="SuperAdmin",["customerId"]=Guid.NewGuid().ToString(),["age"]="99" };
        async Task<long> Scalar(string sql,string? email=null)
        {
            await using var c=await admin.OpenConnectionAsync();
            await using var q=new NpgsqlCommand(sql,c);
            if(email!=null)q.Parameters.AddWithValue("email",email);
            return Convert.ToInt64(await q.ExecuteScalarAsync());
        }
        async Task Execute(string sql,string? email=null)
        {
            await using var c=await admin.OpenConnectionAsync();await using var q=new NpgsqlCommand(sql,c);
            if(email!=null)q.Parameters.AddWithValue("email",email);
            await q.ExecuteNonQueryAsync();
        }
        try
        {
            using var a=New();using var stranger=New();
            Check((await stranger.Http.GetStringAsync("/Account/Register")).Contains("A beautiful beginning."),
                "unchanged pink registration Razor view renders with actual PublicStudioStore and no compatibility views");
            Check((await stranger.Http.GetAsync("/forged")).StatusCode==HttpStatusCode.Unauthorized,"arbitrary ClaimsPrincipal cannot establish trusted session");
            Check((await stranger.Http.PostAsync("/Account/Register",new FormUrlEncodedContent(Registration("csrf@example.test")))).StatusCode==HttpStatusCode.BadRequest,
                "real MVC registration requires antiforgery");
            var email="owner@example.test";
            using(var response=await a.Post("/Account/Register",Registration(email)))
            {
                Check(response.StatusCode==HttpStatusCode.Redirect,"actual MVC registration starts durable attempt");
                var cookie=response.Headers.GetValues("Set-Cookie").Single(x=>x.StartsWith("GlamSignup="));
                Check(cookie.Contains("httponly",StringComparison.OrdinalIgnoreCase) && cookie.Contains("samesite=strict",StringComparison.OrdinalIgnoreCase) &&
                    cookie.Contains("path=/Account",StringComparison.OrdinalIgnoreCase),"signup cookie is protected, HttpOnly and scoped");
            }
            Check(await Scalar("SELECT count(*) FROM public.customers")==0 && !a.HasAuthCookie,"no local account or auth cookie before OTP");
            var creates=fake.Signups;
            Check((await a.Post("/Account/Register",Registration(email))).StatusCode==HttpStatusCode.Redirect && fake.Signups==creates,
                "same-browser repeated registration resumes original attempt without another provider call");
            Check((await stranger.Post("/Account/Register",Registration(email))).StatusCode==HttpStatusCode.ServiceUnavailable && fake.Signups==creates,
                "another browser cannot replace or duplicate pending profile");
            var verifies=fake.Verifications;
            Check((await stranger.Post("/Account/Verify",new(){["email"]=email,["code"]="123456"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Verifications==verifies,"OTP requires the browser-bound protected attempt");
            Check((await a.Post("/Account/Verify",new(){["email"]="other@example.test",["code"]="123456"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Verifications==verifies,"posted cross-account email cannot select another binding");
            Check((await a.Post("/Account/Verify",new(){["email"]=email,["code"]="abc123"})).StatusCode==HttpStatusCode.OK &&
                fake.Verifications==verifies,"malformed OTP denied before provider");
            Check((await a.Post("/Account/Verify",new(){["email"]=email,["code"]="123456",["role"]="SuperAdmin"})).StatusCode==HttpStatusCode.Redirect,
                "OTP provider verification commits Customer binding");
            Check(!a.HasAuthCookie,"OTP completion requires independent password sign-in, not a speculative cookie");
            Check(await Scalar("""
                SELECT count(*) FROM public.customers c JOIN public.account_identities i ON i.customer_id=c.id AND i.id=c.app_id
                JOIN public.account_security_generations g ON g.account_id=i.id
                JOIN public.auth_mvc_signup_attempts a ON a.attempt_id=i.id
                WHERE c.email=@email AND i.admin_id IS NULL AND i.auth_state='Supabase' AND i.credential_sync_state='Ready'
                  AND i.supabase_user_id=a.provider_subject AND i.credential_epoch=1 AND g.generation=1
                  AND a.state='Consumed' AND a.operation_id=a.consumed_operation_id
                  AND c.birthday=DATE '2001-01-01' AND c.suffix='Jr.' AND c.age_at_registration<>99
                """,email)==1,"profile, immutable provider subject, attempt and initial generation persist atomically");
            Check(await Scalar("SELECT count(*) FROM public.admin_users")==0,"first public signup never creates Admin or SuperAdmin");
            Check((await a.Post("/Account/Verify",new(){["email"]=email,["code"]="123456"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                await Scalar("SELECT count(*) FROM public.customers")==1,"consumed OTP attempt cannot create another account");
            Check((await a.Post("/Account/Register",Registration(email))).StatusCode==HttpStatusCode.ServiceUnavailable,
                "registered email cannot be rebound by another signup");
            using(var response=await a.Login(email,"Synthetic123!"))
                Check(response.StatusCode==HttpStatusCode.Redirect && a.HasAuthCookie,"verified password and authoritative user endpoint issue protected cookie");
            Check(await a.Http.GetStringAsync("/protected")=="Customer","durable cookie resolves trusted Customer role despite forged registration role");
            fake.MismatchUser=true;
            using(var probe=New())
            {
                await probe.Login(email,"Synthetic123!");
                Check(!probe.HasAuthCookie,"provider session/user subject mismatch cannot issue cookie");
            }
            fake.MismatchUser=false;
            using(var tampered=New())
            {
                var cookie=a.AuthCookie!;
                tampered.Cookies.Add(new Cookie(cookie.Name,cookie.Value[..^2]+"XX","/",new Uri(address).Host));
                Check((await tampered.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized,"tampered protected cookie denied");
            }
            var collisionDenied=false;
            try
            {
                await Execute("""
                    INSERT INTO public.admin_users(app_id,email,username,role)
                    SELECT app_id,'collision@example.test','collision@example.test','SuperAdmin' FROM public.customers WHERE email=@email
                    """,email);
            }
            catch(PostgresException e) when(e.SqlState==PostgresErrorCodes.UniqueViolation) {collisionDenied=true;}
            Check(collisionDenied && await Scalar("SELECT count(*) FROM public.admin_users")==0,
                "database rejects ambiguous Customer/staff ownership atomically");
            using var b=New();
            await a.Login(email,"Synthetic123!");await b.Login(email,"Synthetic123!");
            Check((await a.Post("/Account/Logout",new())).StatusCode==HttpStatusCode.Redirect,"actual MVC logout revokes local account generation");
            Check((await a.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized &&
                (await b.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized,"logout invalidates copied and other-browser local tickets");
            await a.Login(email,"Synthetic123!");
            await Execute("UPDATE public.customers SET is_active=false WHERE email=@email",email);
            Check((await a.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized,"account disablement rejects previously valid cookie");
            await Execute("UPDATE public.customers SET is_active=true WHERE email=@email",email);
            await a.Login(email,"Synthetic123!");
            await Execute("UPDATE public.customers SET locked_until=now()+interval '1 hour' WHERE email=@email",email);
            Check((await a.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized,"future database lock rejects an existing cookie");
            using(var locked=New())
            {
                await locked.Login(email,"Synthetic123!");
                Check(!locked.HasAuthCookie,"future database lock rejects fresh provider-verified sign-in");
            }
            await Execute("UPDATE public.customers SET locked_until=now()-interval '1 second' WHERE email=@email",email);
            await a.Login(email,"Synthetic123!");
            Check((await a.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.OK,"expired lock allows a fresh verified login");
            await a.Login(email,"Synthetic123!");await b.Login(email,"Synthetic123!");
            fake.PauseNextPasswordLogin=true;
            var racingPassword=a.Post("/Account/ChangePassword",new(){["currentPassword"]="Synthetic123!",["newPassword"]="Racing456!",["confirmPassword"]="Racing456!"});
            await fake.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            try {await b.Post("/Account/Logout",new());}
            finally {fake.ReleaseLogin.TrySetResult();}
            await racingPassword;
            Check(fake.Updates==0 && await Scalar("SELECT credential_epoch FROM public.account_identities WHERE login=@email",email)==1,
                "revocation during fresh password proof prevents provider update under generation lock");
            await a.Login(email,"Synthetic123!");await b.Login(email,"Synthetic123!");
            Check((await a.Post("/Account/ChangePassword",new(){["currentPassword"]="Synthetic123!",["newPassword"]="Changed456!",["confirmPassword"]="Changed456!"})).StatusCode==HttpStatusCode.Redirect,
                "actual MVC password change uses fresh provider proof and durable epoch transition");
            Check(await Scalar("SELECT credential_epoch FROM public.account_identities WHERE login=@email",email)==2 &&
                (await b.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized,"password change invalidates old cookies and persists incremented epoch");
            await a.Login(email,"Synthetic123!");
            Check(!a.HasAuthCookie,"old provider password cannot authenticate via local fallback");
            await a.Login(email,"Changed456!");
            Check((await a.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.OK,"new provider password signs in against synchronized binding");
            using var recovery=New();
            Check((await recovery.Post("/Account/ForgotPassword",new(){["email"]=email})).StatusCode==HttpStatusCode.Redirect,"MVC forgot-password uses existing Supabase adapter");
            Check((await recovery.Post("/Account/ResetPassword",new(){["email"]=email,["code"]="123456",["newPassword"]="Recovered789!",["confirmPassword"]="Recovered789!"})).StatusCode==HttpStatusCode.Redirect &&
                !recovery.HasAuthCookie,"MVC recovery verifies OTP and user, updates password, and never auto-signs in");
            Check(await Scalar("SELECT credential_epoch FROM public.account_identities WHERE login=@email",email)==3 &&
                (await a.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized,"recovery invalidates existing cookies");

            using var race1=New();using var race2=New();
            var competing=await Task.WhenAll(race1.Post("/Account/Register",Registration("race@example.test")),
                race2.Post("/Account/Register",Registration("race@example.test")));
            Check(competing.Count(x=>x.StatusCode==HttpStatusCode.Redirect)==1 &&
                competing.Count(x=>x.StatusCode==HttpStatusCode.ServiceUnavailable)==1,"concurrent first-time registration admits one durable/provider attempt");
            var winner=competing[0].StatusCode==HttpStatusCode.Redirect ? race1 : race2;
            var loser=ReferenceEquals(winner,race1) ? race2 : race1;
            loser.CopySignupFrom(winner);
            var verified=await Task.WhenAll(winner.Post("/Account/Verify",new(){["email"]="race@example.test",["code"]="123456"}),
                loser.Post("/Account/Verify",new(){["email"]="race@example.test",["code"]="123456"}));
            Check(verified.Count(x=>x.StatusCode==HttpStatusCode.Redirect)==1 &&
                await Scalar("SELECT count(*) FROM public.customers WHERE email=@email","race@example.test")==1,
                "concurrent OTP binding consumes exactly one operation and inserts one Customer");

            using var resend=New();
            await resend.Post("/Account/Register",Registration("resend@example.test"));
            Check((await resend.Post("/Account/Resend",new(){["email"]="resend@example.test"})).StatusCode==HttpStatusCode.ServiceUnavailable,
                "MVC resend enforces durable cooldown");
            await Execute("UPDATE public.auth_mvc_signup_attempts SET next_resend_at=now()-interval '1 second' WHERE normalized_email=@email","resend@example.test");
            Check((await resend.Post("/Account/Resend",new(){["email"]="resend@example.test"})).StatusCode==HttpStatusCode.Redirect,
                "MVC resend reuses the existing subject and releases its operation claim");
            using var crossBinding=New();
            fake.ReuseSubject=fake.SubjectFor(email);
            await crossBinding.Post("/Account/Register",Registration("cross-binding@example.test"));
            fake.ReuseSubject=null;
            Check((await crossBinding.Post("/Account/Verify",new(){["email"]="cross-binding@example.test",["code"]="123456"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                await Scalar("SELECT count(*) FROM public.customers WHERE email=@email","cross-binding@example.test")==0,
                "a provider subject already owned by another Customer cannot be rebound");
            using var rollback=New();
            await rollback.Post("/Account/Register",Registration("rollback@example.test"));
            await Execute("""
                CREATE FUNCTION public.fixture_fail_customer_auth() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN IF NEW.email='rollback@example.test' THEN RAISE EXCEPTION 'Synthetic registration failure'; END IF; RETURN NEW; END $$;
                CREATE TRIGGER fixture_fail_customer_auth AFTER INSERT ON public.customers FOR EACH ROW EXECUTE FUNCTION public.fixture_fail_customer_auth()
                """);
            Check((await rollback.Post("/Account/Verify",new(){["email"]="rollback@example.test",["code"]="123456"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                await Scalar("SELECT count(*) FROM public.customers WHERE email=@email","rollback@example.test")==0 &&
                await Scalar("SELECT count(*) FROM public.account_identities WHERE login=@email","rollback@example.test")==0,
                "database failure rolls back Customer, identity and attempt consumption together");
            verifies=fake.Verifications;
            Check((await rollback.Post("/Account/Verify",new(){["email"]="rollback@example.test",["code"]="123456"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Verifications==verifies,"failed binding never automatically replays consumed provider OTP");

            using var interrupted=New();
            fake.TimeoutSignup=true;
            Check((await interrupted.Post("/Account/Register",Registration("interrupted@example.test"))).StatusCode==HttpStatusCode.ServiceUnavailable,
                "uncertain signup returns unavailable rather than success");
            creates=fake.Signups;
            Check((await interrupted.Post("/Account/Register",Registration("interrupted@example.test"))).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Signups==creates && await Scalar("SELECT count(*) FROM public.customers WHERE email=@email","interrupted@example.test")==0,
                "uncertain signup is retained and never automatically repeated or bound");
            fake.TimeoutSignup=false;
            using var mismatch=New();
            await mismatch.Post("/Account/Register",Registration("mismatch@example.test"));
            fake.MismatchUser=true;
            Check((await mismatch.Post("/Account/Verify",new(){["email"]="mismatch@example.test",["code"]="123456"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                await Scalar("SELECT count(*) FROM public.customers WHERE email=@email","mismatch@example.test")==0,
                "OTP/user mismatch leaves durable attempt unresolved with no local identity");
            fake.MismatchUser=false;
            using var schema=New();
            creates=fake.Signups;
            const string customerInsertColumns="app_id,email,first_name,last_name,full_name,age_at_registration,gender,address,password_hash,is_active,suffix,birthday";
            await Execute($"REVOKE INSERT({customerInsertColumns}) ON public.customers FROM glam_v2_backend");
            Check((await schema.Post("/Account/Register",Registration("permission@example.test"))).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Signups==creates,"missing runtime INSERT permission denies before provider signup");
            await Execute($"GRANT INSERT({customerInsertColumns}) ON public.customers TO glam_v2_backend");
            await Execute("ALTER TABLE public.auth_mvc_signup_attempts RENAME TO fixture_missing_attempts");
            Check((await schema.Post("/Account/Register",Registration("missing@example.test"))).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Signups==creates,"missing signup schema denies before provider signup");
            await Execute("ALTER TABLE public.fixture_missing_attempts RENAME TO auth_mvc_signup_attempts");
            app.Configuration["Authentication:DurableSessionEnforcement"]="false";
            Check((await schema.Post("/Account/Register",Registration("gated@example.test"))).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Signups==creates,"dormant application integration cannot write with authentication gate closed");
            app.Configuration["Authentication:DurableSessionEnforcement"]="true";
            await recovery.Login(email,"Recovered789!");
            fake.TimeoutUpdate=true;
            await recovery.Post("/Account/ChangePassword",new(){["currentPassword"]="Recovered789!",["newPassword"]="Uncertain123!",["confirmPassword"]="Uncertain123!"});
            Check(await Scalar("SELECT count(*) FROM public.account_identities WHERE login=@email AND credential_sync_state='ReconciliationRequired'",email)==1 &&
                !recovery.HasAuthCookie,"uncertain password update is fenced, rejects tickets and requires reconciliation");
            await surviving.Login("race@example.test","Synthetic123!");
            Check(surviving.HasAuthCookie,"valid durable ticket saved for host restart");
            await pending.Post("/Account/Register",Registration("restart@example.test"));
            Console.WriteLine($"{passed} Customer MVC/PostgreSQL integration checks passed; fake Supabase only.");
        }
        finally {await app.StopAsync();await app.DisposeAsync();}
        var restarted=BuildHost(backend,fake,keys,certificate);
        await restarted.StartAsync();
        try
        {
            var nextAddress=restarted.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var restored=new Client(nextAddress,certificate,surviving.Cookies);
            using var pendingRestored=new Client(nextAddress,certificate,pending.Cookies);
            Check(await restored.Http.GetStringAsync("/protected")=="Customer","encrypted persistent keys preserve cookie across host restart");
            Check((await pendingRestored.Post("/Account/Verify",new(){["email"]="restart@example.test",["code"]="123456"})).StatusCode==HttpStatusCode.Redirect,
                "persistent key ring decrypts pending browser cookie and encrypted signup profile after restart");
            var options=restarted.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(CookieAuthenticationDefaults.AuthenticationScheme);
            var ticket=options.TicketDataFormat.Unprotect(restored.AuthCookie!.Value)!;
            ticket.Properties.ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(-1);
            using var expired=new Client(nextAddress,certificate);
            expired.Cookies.Add(new Cookie(restored.AuthCookie.Name,options.TicketDataFormat.Protect(ticket),"/","127.0.0.1"){Secure=true});
            Check((await expired.Http.GetAsync("/protected")).StatusCode==HttpStatusCode.Unauthorized,"expired protected ticket is rejected by actual cookie handler");
            Check(Directory.GetFiles(keys,"key-*.xml").All(file=>System.Xml.Linq.XDocument.Load(file)
                .Descendants().Any(e=>e.Name.LocalName=="encryptedSecret")),"persisted synthetic signing keys are certificate-encrypted at rest");
        }
        finally {await restarted.StopAsync();await restarted.DisposeAsync();}
        await GuardedSurface(backend,fake,keys,certificate,Check);
        Console.WriteLine($"{passed} total Customer MVC/HTTPS/persistence checks passed; fake provider and disposable PostgreSQL only.");
    }

    private static WebApplication BuildHost(NpgsqlDataSource backend,FakeCustomerSupabase fake,string keys,X509Certificate2 certificate,bool guarded=false)
    {
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions {Args=[],EnvironmentName="Development"});
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelHttpsConfiguration().UseUrls("https://127.0.0.1:0")
            .ConfigureKestrel(o=>o.ConfigureHttpsDefaults(https=>https.ServerCertificate=certificate));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string,string?> {
            ["Authentication:DurableSessionEnforcement"]="true",["Database:Schema"]="public",["Studio:TimeZone"]="Asia/Manila",
            ["Supabase:Url"]="https://auth.example.test",["Supabase:AnonKey"]="synthetic-public-key",
            ["Deployment:Profile"]=guarded ? CustomerAuthIntegrationProfile.Name : null,
            ["Authentication:IntegrationCustomerEmail"]="race@example.test" });
        builder.Services.AddSingleton(backend);
        CustomerAuthIntegrationProfile.ConfigureKeyRing(builder.Services,keys,"synthetic-local",certificate);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMemoryCache();
        builder.Services.AddAntiforgery(o=>o.HeaderName="X-CSRF-TOKEN");
        builder.Services.AddStudioCookieAuthentication(false,true);
        builder.Services.Configure<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme,o=> {
            CustomerAuthIntegrationProfile.ConfigureCookie(o);
            o.Events.OnRedirectToLogin=c=> {c.Response.StatusCode=401;return Task.CompletedTask;};
        });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(o=> {foreach(var name in RateLimitPolicies.All.Keys)
            o.AddPolicy(name,_=>System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("isolated-auth"));});
        builder.Services.AddDbContext<PublicMakeupDbContext>(o=>o.UseNpgsql(backend));
        builder.Services.AddScoped<IAppFileStore,LocalAppFileStore>();
        builder.Services.AddScoped<IStudioStore,PublicStudioStore>();
        builder.Services.AddSingleton<IHttpClientFactory>(fake);
        builder.Services.AddScoped<SupabaseAuthService>();
        builder.Services.AddSingleton(new LoginAttemptLimiter(new AuthenticationTestClock()));
        builder.Services.AddSingleton<EmailVerificationService>();
        builder.Services.AddSingleton<BrevoEmailSender>();
        builder.Services.AddScoped<AccountSettingsService>();
        builder.Services.AddScoped<AvatarStorageService>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IInvitationService,MemoryInvitationService>();
        builder.Services.AddPersistentCustomerAuthentication();
        builder.Services.AddControllersWithViews(o=>o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
            .AddApplicationPart(typeof(AccountController).Assembly);
        var app=builder.Build();
        if(guarded)CustomerAuthIntegrationProfile.UseBoundary(app);
        app.UseRouting();app.UseAuthentication();
        if(guarded)CustomerAuthIntegrationProfile.UseAuthenticatedPageRedirect(app);
        app.UseAuthorization();app.UseRateLimiter();
        app.MapControllerRoute("mvc","{controller=Account}/{action=Login}/{id?}");
        if(guarded)CustomerAuthIntegrationProfile.MapSessionProbe(app);
        else
        {
            app.MapGet("/csrf",(HttpContext c,IAntiforgery a)=>a.GetAndStoreTokens(c).RequestToken!);
            app.MapGet("/protected",async (HttpContext c,TrustedRequestSessionAccessor accessor)=> {
                var session=await accessor.ResolveAsync();
                return session!=null && session.TryGetActor(c,out var actor) && actor!=null
                    ? Results.Text(actor.Role) : Results.Unauthorized();
            });
            app.MapGet("/forged",async (HttpContext c,TrustedRequestSessionAccessor accessor)=> {
                c.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.Role,"SuperAdmin")],"forged"));
                return await accessor.ResolveAsync()==null ? Results.Unauthorized() : Results.Ok();
            });
        }
        return app;
    }

    internal static async Task SurfaceSmoke(X509Certificate2 certificate)
    {
        using var tls=X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx),null,X509KeyStorageFlags.DefaultKeySet);
        await using var source=NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=synthetic;Username=synthetic;Password=synthetic;SSL Mode=Disable;Timeout=1");
        var app=BuildHost(source,new FakeCustomerSupabase(),Path.GetFullPath(Path.Combine("temp","v2-auth-smoke-"+Guid.NewGuid().ToString("N"))),tls);
        await app.StartAsync();
        try
        {
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client=new Client(address,certificate);
            var html=await client.Http.GetStringAsync("/Account/Register");
            if(!html.Contains("A beautiful beginning."))throw new InvalidOperationException("Registration view not rendered.");
            Console.WriteLine("PASS real HTTPS/Razor registration smoke without database access");
        }
        finally {await app.StopAsync();await app.DisposeAsync();}
    }

    private static async Task GuardedSurface(NpgsqlDataSource backend,FakeCustomerSupabase fake,string keys,X509Certificate2 certificate,Action<bool,string> check)
    {
        var app=BuildHost(backend,fake,keys,certificate,true);
        await app.StartAsync();
        try
        {
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client=new Client(address,certificate,guarded:true);
            foreach(var path in new[]{"/Admin","/Bookings","/Bookings/Create","/Account/AcceptInvite","/Account/Settings","/Account/SaveProfile","/storage-migrate"})
                check((await client.Http.PostAsync(path,new StringContent(""))).StatusCode==HttpStatusCode.NotFound,"isolated surface blocks "+path);
            var calls=fake.Signups;
            check((await client.Post("/Account/Register",new(){["email"]="not-allowlisted@example.test",
                ["firstName"]="Synthetic",["lastName"]="Customer",["birthday"]="2001-01-01",["gender"]="Others",
                ["address"]="Synthetic address",["password"]="Synthetic123!"})).StatusCode==HttpStatusCode.ServiceUnavailable &&
                fake.Signups==calls,"single-email allowlist denies before any provider signup");
            using(var response=await client.Login("race@example.test","Synthetic123!"))
                check(response.StatusCode==HttpStatusCode.Redirect && response.Headers.Location?.OriginalString=="/auth-test/session",
                    "isolated MVC login redirects to validated probe, never legacy booking pages");
            check((await client.Http.GetStringAsync("/auth-test/session")).Contains("\"role\":\"Customer\""),"isolated probe revalidates trusted Customer session");
            check((await client.Post("/Account/Logout",new())).StatusCode==HttpStatusCode.Redirect,"isolated logout accepts real antiforgery token and revokes generation");
            check((await client.Http.GetAsync("/auth-test/session")).StatusCode==HttpStatusCode.Unauthorized,"isolated probe rejects logged-out session");
        }
        finally {await app.StopAsync();await app.DisposeAsync();}
    }

    private sealed class Client : IDisposable
    {
        internal CookieContainer Cookies {get;}=new();
        internal HttpClient Http {get;}
        private readonly Uri origin;
        private readonly bool guarded;
        internal Client(string address,X509Certificate2 certificate,CookieContainer? cookies=null,bool guarded=false)
        {
            this.guarded=guarded;
            if(cookies!=null)Cookies=cookies;
            origin=new Uri(address);
            Http=new HttpClient(new HttpClientHandler {AllowAutoRedirect=false,CookieContainer=Cookies,
                ServerCertificateCustomValidationCallback=(_,server,_,_)=>server?.GetCertHashString()==certificate.GetCertHashString()}) {BaseAddress=origin};
        }
        internal Cookie? AuthCookie=>Cookies.GetCookies(origin).Cast<Cookie>().FirstOrDefault(x=>x.Name=="__Host-GlamV2.CustomerAuthTest");
        internal bool HasAuthCookie=>AuthCookie!=null;
        internal void CopySignupFrom(Client other)
        {
            var cookie=other.Cookies.GetCookies(new Uri(other.origin,"/Account/Verify")).Cast<Cookie>().Single(x=>x.Name=="GlamSignup");
            Cookies.Add(new Cookie(cookie.Name,cookie.Value,"/Account",origin.Host));
        }
        internal async Task<HttpResponseMessage> Post(string path,Dictionary<string,string> form)
        {
            string token;
            if(guarded)
            {
                using var response=await Http.GetAsync("/Account/Login");
                if(response.StatusCode==HttpStatusCode.Redirect)
                {
                    using var json=JsonDocument.Parse(await Http.GetStringAsync("/auth-test/session"));
                    token=json.RootElement.GetProperty("csrfToken").GetString()!;
                }
                else token=WebUtility.HtmlDecode(Regex.Match(await response.Content.ReadAsStringAsync(),"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
            }
            else token=await Http.GetStringAsync("/csrf");
            using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=new FormUrlEncodedContent(form)};
            request.Headers.Add("X-CSRF-TOKEN",token);
            return await Http.SendAsync(request);
        }
        internal Task<HttpResponseMessage> Login(string email,string password)=>Post("/Account/Login",new(){["email"]=email,["password"]=password,["role"]="SuperAdmin"});
        public void Dispose()=>Http.Dispose();
    }
}

internal sealed class AuthenticationTestClock : TimeProvider
{
    private long ticks=DateTimeOffset.UtcNow.UtcTicks;
    // Many legitimate sign-ins in one fixture should not test a one-minute rate window.
    public override DateTimeOffset GetUtcNow()=>new(Interlocked.Add(ref ticks,2*TimeSpan.TicksPerMinute),TimeSpan.Zero);
}
