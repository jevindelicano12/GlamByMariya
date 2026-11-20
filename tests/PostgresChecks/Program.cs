using System.Security.Claims;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;

if(args.Length!=3 || !int.TryParse(args[0],out var port) || port is < 1024 or > 65535 ||
   !Guid.TryParseExact(args[1],"N",out var marker) || marker==Guid.Empty)
    throw new Exception("Run via tests/postgres-integration-checks.mjs; a fresh marked loopback cluster is required.");
// This test executable is deliberately restricted to a throwaway LOOPBACK server.
var connection=$"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;GSS Encryption Mode=Disable;Pooling=false";
var builder = new NpgsqlDataSourceBuilder(connection);
async Task Guard(NpgsqlConnection physical)
{
    await using var command = new NpgsqlCommand("""
        SELECT current_database()='postgres' AND current_user='postgres'
          AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet
          AND inet_server_port()=@port AND current_setting('glam.payment_review_marker',true)=@marker
        """, physical);
    command.Parameters.AddWithValue("port",port);
    command.Parameters.AddWithValue("marker",marker.ToString("N"));
    if ((bool?)await command.ExecuteScalarAsync() != true) throw new System.Security.SecurityException("Unsafe PostgreSQL physical connection.");
}
builder.UsePhysicalConnectionInitializer(c => Guard(c).GetAwaiter().GetResult(), Guard);
await using var source = builder.Build();
await using (var guard = await source.OpenConnectionAsync())
{
    using var command = new NpgsqlCommand("""
        SELECT to_regnamespace('makeup') IS NULL
          AND NOT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public')
          AND NOT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public')
        """,guard);
    if (command.ExecuteScalar() is not true) throw new InvalidOperationException("Unsafe or nonempty PostgreSQL test target.");
}
var root=Directory.GetCurrentDirectory();
var temp=Path.GetFullPath(args[2]);
var owner=Path.Combine(temp,"owner-marker");
if (!Directory.Exists(temp) || (File.GetAttributes(temp) & FileAttributes.ReparsePoint)!=0 ||
    !File.Exists(owner) || (File.GetAttributes(owner) & FileAttributes.ReparsePoint)!=0 ||
    File.ReadAllText(owner)!=marker.ToString("N") || Directory.GetFileSystemEntries(temp).Length!=1)
    throw new InvalidOperationException("Unsafe or nonempty synthetic artifact directory.");
var env=new TestEnvironment { ContentRootPath=temp };
var config=new ConfigurationBuilder().AddInMemoryCollection().Build();
var http=new HttpContextAccessor { HttpContext=new DefaultHttpContext() };
using var db=new MakeupDbContext(new DbContextOptionsBuilder<MakeupDbContext>().UseNpgsql(source).Options);
var count=0;
void Check(bool condition,string message) { if(!condition) throw new Exception(message); count++; Console.WriteLine("PASS: "+message); }
void Reject(Action action,string message) { try { action(); } catch(InvalidOperationException) { Check(true,message); return; } throw new Exception("Expected rejection: "+message); }
void Login(AppUser user) => http.HttpContext.User=new ClaimsPrincipal(new ClaimsIdentity(new[] {
    new Claim(ClaimTypes.NameIdentifier,user.Id.ToString()),new Claim(ClaimTypes.Role,user.Role),new Claim(ClaimTypes.Email,user.Email) },"test"));
var proofBytes=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6cWQAAAAASUVORK5CYII=");
string SyntheticProof(byte marker)
{
    var directory=Path.Combine(temp,"App_Data","Proofs");
    Directory.CreateDirectory(directory);
    var path=Path.Combine(directory,Guid.NewGuid()+".png");
    File.WriteAllBytes(path,[..proofBytes,marker]);
    return path;
}
try
{
    var schema=File.ReadAllText(Path.Combine(root,"Frontend","GlamByMariya.Web","MakeupBookingDatabase.sql"));
    var production=schema[..schema.IndexOf("-- 9. Baseline settings/catalog",StringComparison.Ordinal)]
        +File.ReadAllText(Path.Combine(root,"Backend","GlamByMariya.Infrastructure","Data","Sql","ProductionSeed.sql"))+"\nCOMMIT;";
    await DatabaseSetup.ExecuteScriptAsync(db,production);
    var store=new PostgresStudioStore(db,http,env,config,new LocalAppFileStore(env));
    Check(!db.Set<UserRow>().Any() && !db.Set<BookingRow>().Any(),"Production initializer inserts no demo accounts or transactions");
    Check(store.Services.Count==3 && store.AddOns.Count==3,"EF Core reads all services/add-ons and normalized categories");
    Check(store.Settings.OpenHour==8 && store.Settings.BufferMinutes==45,"EF Core reads typed studio settings and service areas");
    var admin=store.AddUser("Studio","Artist","artist@example.test","ArtistPassword123!","Admin",35,"","Studio address on file");
    var customer=store.AddUser("Maria","Santos","maria@example.test","CustomerPassword123!","Customer",25,"Female","24 Test Street, Demo Barangay");
    // A throwaway public-schema fixture proves inspection preserves existing rows
    // and never serializes their values, even without backend SELECT permission.
    db.Database.ExecuteSqlRaw("CREATE TABLE public.integration_existing_records(id bigint PRIMARY KEY,label text NOT NULL); INSERT INTO public.integration_existing_records VALUES(1,'PRIVATE-ROW-NOT-IN-REPORT')");
    await DatabaseSetup.ExecuteScriptAsync(db,File.ReadAllText(Path.Combine(root,"Backend","GlamByMariya.Infrastructure","Data","Sql","BackendPermissions.sql")));
    // This test deliberately changes session role. Own its open reference;
    // script helpers now correctly release theirs instead of pinning a session.
    db.Database.OpenConnection();
    db.Database.ExecuteSqlRaw("SET ROLE makeup_backend");
    Check(db.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").Single()=="makeup_backend","Integration runs under non-owner RLS backend role, not superuser");
    var publicConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Database:Schema"]="public" }).Build();
    var inspectionPath=await DatabaseInspection.InspectAsync(db,publicConfig,temp);
    var inspection=File.ReadAllText(inspectionPath);
    Check(inspection.Contains("integration_existing_records") && inspection.Contains("bigint"),"Read-only inspector inventories existing public tables and column types");
    Check(!inspection.Contains("PRIVATE-ROW-NOT-IN-REPORT") && !inspection.Contains("CustomerPassword123!"),"Inspection report contains no record values or credentials");
    Reject(()=>DatabaseInspection.VerifyApplicationAsync(db,publicConfig).GetAwaiter().GetResult(),"Startup blocks public-schema writes until existing adapter mappings are reviewed");
    Check(store.FindUser("MARIA@example.test")?.Id==customer.Id,"Registration persists and email lookup is case-insensitive");
    Reject(()=>store.AddUser("Other","Customer","MARIA@example.test","CustomerPassword123!","Customer",25,"","24 Test Street"),"Duplicate registration is rejected safely");
    Login(admin);
    var soft=store.Services.Single(x=>x.Name=="Soft Glam");
    var lash=store.AddOns.Single(x=>x.Name=="Lash application");
    var time=store.Now.Date.AddDays(7).AddHours(8);
    Login(customer);
    Reject(()=>store.SubmitBooking(customer.Id,soft.Id,[],time,"24 Test Street","Quezon City",300,"GCash","NO-CONSENT",null,true,false,1,"auto", latitude: 14.5547m, longitude: 121.0244m),"Database booking requires explicit travel-reminder acknowledgment");
    var firstProof=SyntheticProof(1);
    var b=store.SubmitBooking(customer.Id,soft.Id,[lash.Id],time,"24 Test Street","Quezon City",1175,"GCash","GROUP-001",firstProof,true,true,4,"group", latitude: 14.5547m, longitude: 121.0244m);
    Check(b.Total==2350 && b.RequiredDownPayment==1175 && b.End==time.AddMinutes(255),"Database-backed group booking preserves correct prices and duration");
    Check(store.Payments.Single().Reference=="GROUP-001" && !store.Payments.Single().Verified,"Manual deposit remains pending until admin verification");
    var oldReview=store.ReviewPayments(b.Id).Single();
    var revisedProof=SyntheticProof(2);
    store.CorrectPayment(b.Id,customer.Id,"CORRECTED-001",revisedProof);
    Check(store.Payments.Single().Reference=="CORRECTED-001","Payment proof/reference correction persists");
    Login(admin);
    Reject(()=>store.ManageBooking(b.Id,"verify",admin.Id,"",0,""),"Missing review contract cannot verify");
    Reject(()=>store.ManageBooking(b.Id,"verify",admin.Id,"",0,"",oldReview.Id,oldReview.PersistentReviewRevision,oldReview.PersistentReviewVersion),"Correction-first rejects the stale reviewed claim");
    var review=store.ReviewPayments(b.Id).Single();
    Check(review.Reference=="CORRECTED-001" && review.ProofKey!=oldReview.ProofKey && store.ReadAsset(review.ProofKey)?.Bytes.SequenceEqual(File.ReadAllBytes(revisedProof))==true,"Reviewed proof and reference match the corrected payment");
    Reject(()=>store.ManageBooking(b.Id,"verify",admin.Id,"",0,"",review.Id,review.PersistentReviewRevision,"bad"),"Malformed review version cannot verify");
    Reject(()=>store.ManageBooking(b.Id,"verify",customer.Id,"",0,"",review.Id,review.PersistentReviewRevision,review.PersistentReviewVersion),"Customer cannot perform Admin verification");
    var retained=store.ReviewPayments(b.Id).Single();
    Check(!retained.Verified && retained.Reference==review.Reference && retained.ProofKey==review.ProofKey
        && retained.PersistentReviewRevision==review.PersistentReviewRevision && retained.PersistentReviewVersion==review.PersistentReviewVersion
        && store.Bookings.Single(x=>x.Id==b.Id).Status=="Pending Payment","Rejected reviews roll back without changing payment or booking");
    var controller=new AdminController(store,new BookingService(store,store,store,store,store),null!,null!,null!,null!) { ControllerContext=new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext=http.HttpContext! } };
    Check(controller.Proof(oldReview.Id,b.Id,oldReview.PersistentReviewRevision,oldReview.PersistentReviewVersion) is Microsoft.AspNetCore.Mvc.ConflictObjectResult,"Stale proof link is rejected");
    Check(controller.Proof(review.Id,b.Id,review.PersistentReviewRevision,review.PersistentReviewVersion) is Microsoft.AspNetCore.Mvc.FileContentResult exact && exact.FileContents.SequenceEqual(File.ReadAllBytes(revisedProof)),"Proof link returns the exact reviewed synthetic proof");
    store.ManageBooking(b.Id,"verify",admin.Id,"",0,"",review.Id,review.PersistentReviewRevision,review.PersistentReviewVersion);
    store.ManageBooking(b.Id,"verify",admin.Id,"",0,"",review.Id,review.PersistentReviewRevision,review.PersistentReviewVersion);
    b=store.Bookings.Single();
    Check(b.Status=="Confirmed" && b.VerifiedPaid==1175 && b.RemainingBalance==1175,"Verification atomically confirms booking and derives paid/balance");
    Check(store.Payments.Count==1,"Repeated database verification is idempotent");
    Login(customer);
    Reject(()=>store.CorrectPayment(b.Id,customer.Id,"AFTER-VERIFICATION",null),"Verification-first rejects later correction");
    Login(admin);
    soft.Price=900; soft.GroupPrice=700; store.SaveCatalog("service",soft,admin.Id);
    Check(store.Bookings.Single().Total==2350 && store.Services.Single(x=>x.Id==soft.Id).Price==900,"Catalog edits persist without repricing existing bookings");
    soft.Price=600; soft.GroupPrice=525; store.SaveCatalog("service",soft,admin.Id);
    Reject(()=>store.ManageBooking(b.Id,"proof",admin.Id,"New proof",0,""),"Confirmed deposit cannot be reverted by requesting proof");
    Login(customer);
    store.RequestReschedule(b.Id,customer.Id,time.AddDays(1));
    Check(store.Bookings.Single().Start==time && store.Bookings.Single().RequestedStart==time.AddDays(1),"Reschedule request persists while original slot stays reserved");
    Login(admin);
    store.ManageBooking(b.Id,"reschedule",admin.Id,"",0,"");
    Check(store.Bookings.Single().Start==time.AddDays(1) && store.Bookings.Single().RequestedStart==null,"Admin reschedule approval atomically moves slot");
    var block=new AvailabilityBlock(Guid.NewGuid(),time.AddDays(2),time.AddDays(2).AddHours(2),"Artist unavailable");
    store.AddBlock(block,admin.Id);
    Check(store.Blocks.Single().Reason=="Artist unavailable","Availability blocks persist through EF Core");
    Reject(()=>store.AddBlock(new(Guid.NewGuid(),time.AddDays(1),time.AddDays(1).AddHours(1),"Overlap"),admin.Id),"Database rejects blocks overlapping confirmed bookings");
    store.RemoveBlock(block.Id,admin.Id);
    Check(store.Blocks.Count==0,"Admin can remove an availability block");
    var png=proofBytes;
    store.SaveCatalogImage("service",soft.Id,"card",png);
    store.SaveCatalogImage("service",soft.Id,"preview",png);
    var card=store.CatalogImageKey("service",soft.Id,"card");
    Check(card!=null && store.ReadAsset(card)?.Bytes.SequenceEqual(png)==true,"Catalog asset metadata and private local storage round-trip");
    store.RemoveCatalogImage("service",soft.Id,"preview");
    Check(store.CatalogImageKey("service",soft.Id,"preview")==null && store.CatalogImageKey("service",soft.Id,"card")==card,"Card/preview images remain independent");
    store.SavePaymentQr("GCash",png);
    Check(store.ReadAsset(store.PaymentQrKey("GCash"))?.Bytes.SequenceEqual(png)==true,"Payment QR metadata persists for selected method");
    store.RemovePaymentQr("GCash");
    Check(store.PaymentQrKey("GCash")==null,"Admin can remove persisted QR association");
    var settings=store.Settings; settings.BufferMinutes=60; settings.TravelFee=500; store.SaveSettings(settings,admin.Id);
    Check(store.Settings.BufferMinutes==60 && store.Bookings.Single().BufferMinutesSnapshot==45,"Settings edits do not change existing reservation buffer");
    Login(customer);
    store.CancelBooking(b.Id,customer.Id,true);
    b=store.Bookings.Single();
    Check(b.Status=="Cancelled" && b.CancellationFee==200 && b.RefundDue==975,"Customer cancellation preserves PHP 200 fee and verified refund obligation");
    Login(admin);
    store.ManageBooking(b.Id,"refund",admin.Id,"",975,"REFUND-001");
    Check(store.Bookings.Single().Refunded==975 && store.Bookings.Single().PaymentStatus=="Refunded","Manual refund ledger updates financial read model");
    Reject(()=>store.ManageBooking(b.Id,"refund",admin.Id,"",1,"REFUND-OVER"),"Refund overpayment is rejected transactionally");
    store.DeleteCatalog("service",soft,admin.Id);
    Check(store.Services.Single(x=>x.Id==soft.Id).Archived,"Referenced catalog item is archived rather than deleted");
    var extra=new CatalogItem { Name="Temporary extra",Price=100,Minutes=0 };
    store.SaveCatalog("addon",extra,admin.Id); store.DeleteCatalog("addon",extra,admin.Id);
    Check(!store.AddOns.Any(x=>x.Id==extra.Id),"Unused catalog item can be added and deleted");
    Check(store.AuditLogs.Count>0,"Database writes retain readable audit records");
    Login(customer);
    Check(store.Notifications.Count>0,"Customer receives persisted booking notifications");
    var other=store.AddUser("Other","Customer","other@example.test","CustomerPassword123!","Customer",25,"","24 Other Test Street");
    Login(other);
    Check(store.Bookings.Count==0 && store.Payments.Count==0,"Customer read models do not load another customer's booking/payment data");
    Reject(()=>store.CancelBooking(b.Id,other.Id,true),"Another customer cannot cancel the booking");
    Login(admin);
    var full=store.Services.Single(x=>x.Name=="Glamour Makeup");
    Login(customer);
    var first=store.SubmitBooking(customer.Id,full.Id,[],time.AddDays(20),"24 Test Street","Quezon City",375,"Maya","RACE-001",SyntheticProof(3),true,true,1,"auto", latitude: 14.5547m, longitude: 121.0244m);
    var second=store.SubmitBooking(customer.Id,full.Id,[],time.AddDays(20),"24 Test Street","Quezon City",375,"Maya","RACE-002",SyntheticProof(4),true,true,1,"auto", latitude: 14.5547m, longitude: 121.0244m);
    Login(admin);
    var firstReview=store.ReviewPayments(first.Id).Single();
    var secondReview=store.ReviewPayments(second.Id).Single();
    async Task<bool> ConfirmInSeparateConnection(Guid id,Payment claim) => await Task.Run(()=>
    {
        using var independent=new MakeupDbContext(new DbContextOptionsBuilder<MakeupDbContext>().UseNpgsql(source).Options);
        independent.Database.OpenConnection(); independent.Database.ExecuteSqlRaw("SET ROLE makeup_backend");
        var commandStore=new PostgresStudioStore(independent,http,env,config,new LocalAppFileStore(env));
        try { commandStore.ManageBooking(id,"verify",admin.Id,"",0,"",claim.Id,claim.PersistentReviewRevision,claim.PersistentReviewVersion); return true; }
        catch(InvalidOperationException) { return false; }
    });
    var race=await Task.WhenAll(ConfirmInSeparateConnection(first.Id,firstReview),ConfirmInSeparateConnection(second.Id,secondReview));
    Check(race.Count(x=>x)==1,"Two native PostgreSQL connections cannot confirm overlapping appointments");
    var racePayments=store.Payments.Where(x=>x.BookingId==first.Id || x.BookingId==second.Id).ToList();
    Check(racePayments.Count(x=>x.Verified)==1,"Losing concurrent confirmation does not partially verify its payment");
    Login(customer);
    var reviewRace=store.SubmitBooking(customer.Id,full.Id,[],time.AddDays(28),"24 Test Street","Quezon City",375,"Maya","REVIEW-RACE",SyntheticProof(5),true,true,1,"auto", latitude: 14.5547m, longitude: 121.0244m);
    Login(admin);
    var raceReview=store.ReviewPayments(reviewRace.Id).Single();
    var replacement=SyntheticProof(6);
    using (var barrier=new Barrier(2))
    {
        Task<bool> VerifyRace() => Task.Run(() => {
            using var independent=new MakeupDbContext(new DbContextOptionsBuilder<MakeupDbContext>().UseNpgsql(source).Options);
            independent.Database.OpenConnection(); independent.Database.ExecuteSqlRaw("SET ROLE makeup_backend");
            var actor=new HttpContextAccessor { HttpContext=new DefaultHttpContext { User=new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier,admin.Id.ToString()),new Claim(ClaimTypes.Role,admin.Role),new Claim(ClaimTypes.Email,admin.Email) },"test")) } };
            var candidate=new PostgresStudioStore(independent,actor,env,config,new LocalAppFileStore(env));
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Verification race peer did not start.");
            try { candidate.ManageBooking(reviewRace.Id,"verify",admin.Id,"",0,"",raceReview.Id,raceReview.PersistentReviewRevision,raceReview.PersistentReviewVersion); return true; }
            catch(InvalidOperationException e) when(e.Message.Contains("Reload and inspect")) { return false; }
        });
        Task<bool> CorrectRace() => Task.Run(() => {
            using var independent=new MakeupDbContext(new DbContextOptionsBuilder<MakeupDbContext>().UseNpgsql(source).Options);
            independent.Database.OpenConnection(); independent.Database.ExecuteSqlRaw("SET ROLE makeup_backend");
            var actor=new HttpContextAccessor { HttpContext=new DefaultHttpContext { User=new ClaimsPrincipal(new ClaimsIdentity(new[] {
                new Claim(ClaimTypes.NameIdentifier,customer.Id.ToString()),new Claim(ClaimTypes.Role,customer.Role),new Claim(ClaimTypes.Email,customer.Email) },"test")) } };
            var candidate=new PostgresStudioStore(independent,actor,env,config,new LocalAppFileStore(env));
            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Correction race peer did not start.");
            try { candidate.CorrectPayment(reviewRace.Id,customer.Id,"REVIEW-RACE-NEW",replacement); return true; }
            catch(InvalidOperationException e) when(e.Message.Contains("Only unverified pending deposits")) { return false; }
        });
        var outcomes=await Task.WhenAll(VerifyRace(),CorrectRace());
        var after=store.ReviewPayments(reviewRace.Id).Single();
        Check(outcomes.Count(x=>x)==1,"Concurrent correction and verification have exactly one winner");
        Check(outcomes[0] ? after.Verified && after.ProofKey==raceReview.ProofKey && after.Reference==raceReview.Reference
            : !after.Verified && after.ProofKey!=raceReview.ProofKey && after.Reference=="REVIEW-RACE-NEW",
            "Concurrent outcome never verifies a replacement proof the Admin did not review");
    }
    var parsed=DatabaseConfiguration.ConnectionString(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["ConnectionStrings:MakeupDatabase"]="postgresql://postgres:encoded%40password@db.example.test:5432/postgres" }).Build());
    Check(new NpgsqlConnectionStringBuilder(parsed!).SslMode==SslMode.VerifyFull,"Supabase configuration requires certificate and hostname verification");
    const string target="independent-placeholder";
    DatabaseConfiguration.ValidateTarget(new NpgsqlConnectionStringBuilder { Host=$"db.{target}.supabase.co",Username="postgres" },target);
    Check(true,"Target identity guard accepts the requested project's direct endpoint");
    DatabaseConfiguration.ValidateTarget(new NpgsqlConnectionStringBuilder { Host="test-region.pooler.supabase.com",Username="postgres."+target },target);
    Check(true,"Target identity guard accepts project-qualified pooler credentials without guessing a region");
    Reject(()=>DatabaseConfiguration.ValidateTarget(new NpgsqlConnectionStringBuilder { Host="db.vvqiofpqebucwsgkxqdg.supabase.co",Username="postgres" },target),"Old project connection is rejected before any network or data operation");
    db.Database.ExecuteSqlRaw("RESET ROLE");
    Check(db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM public.integration_existing_records WHERE label='PRIVATE-ROW-NOT-IN-REPORT'").Single()==1,"Inspection preserves pre-existing public records");
    var authenticationMessage=DatabaseDiagnostics.Describe(new PostgresException("PRIVATE-DIAGNOSTIC-MARKER","FATAL","FATAL","28P01"));
    Check(authenticationMessage.Contains("authentication failed") && !authenticationMessage.Contains("PRIVATE-DIAGNOSTIC-MARKER"),"Database authentication diagnostics do not expose raw provider details");
    var tlsMessage=DatabaseDiagnostics.Describe(new System.Security.Authentication.AuthenticationException("PRIVATE-DIAGNOSTIC-MARKER"));
    Check(tlsMessage.Contains("TLS") && !tlsMessage.Contains("PRIVATE-DIAGNOSTIC-MARKER"),"TLS diagnostics do not expose raw exception details or disable verification");
    var tenantMessage=DatabaseDiagnostics.Describe(new PostgresException("Tenant or user not found: PRIVATE-DIAGNOSTIC-MARKER","FATAL","FATAL","XX000"));
    Check(tenantMessage.Contains("pooler") && !tenantMessage.Contains("PRIVATE-DIAGNOSTIC-MARKER"),"Supabase tenant diagnostics identify endpoint problems without leaking input");
    Console.WriteLine($"All {count} EF Core/PostgreSQL integration checks passed.");
}
catch(Exception e)
{
    Console.Error.WriteLine(e.ToString()); Environment.ExitCode=1;
}
finally
{
    db.Database.CloseConnection();
    Console.WriteLine("Synthetic proof artifacts retained: "+temp);
}

sealed class TestEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; }="PostgresChecks";
    public string EnvironmentName { get; set; }="Development";
    public string ContentRootPath { get; set; }="";
    public IFileProvider ContentRootFileProvider { get; set; }=new NullFileProvider();
    public string WebRootPath { get; set; }="";
    public IFileProvider WebRootFileProvider { get; set; }=new NullFileProvider();
}
