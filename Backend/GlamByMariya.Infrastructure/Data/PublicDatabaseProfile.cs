using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace GlamByMariya.Infrastructure.Data;

public static class PublicDatabaseProfile
{
    public static async Task RunAsync(MakeupDbContext db,string root)
    {
        await using var connectionLease = await DatabaseConnectionLease.OpenAsync(db.Database);
        await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var counts=await db.Database.SqlQueryRaw<CountRow>("""
            SELECT 'customers'::text AS "Name",count(*) AS "Count" FROM public.customers
            UNION ALL SELECT 'admin_users',count(*) FROM public.admin_users
            UNION ALL SELECT 'makeup_artists',count(*) FROM public.makeup_artists
            UNION ALL SELECT 'services',count(*) FROM public.services
            UNION ALL SELECT 'reservations',count(*) FROM public.reservations
            UNION ALL SELECT 'payments',count(*) FROM public.payments
            UNION ALL SELECT 'blocked_slots',count(*) FROM public.blocked_slots
            UNION ALL SELECT 'artist_availability',count(*) FROM public.artist_availability
            UNION ALL SELECT 'reservation_history',count(*) FROM public.reservation_history
            """).ToListAsync();
        var statuses=await db.Database.SqlQueryRaw<StatusCountRow>("""
            SELECT 'reservations'::text AS "Table",status::text AS "Status",count(*) AS "Count" FROM public.reservations GROUP BY status
            UNION ALL SELECT 'payments',status::text,count(*) FROM public.payments GROUP BY status
            """).ToListAsync();
        var issues=await db.Database.SqlQueryRaw<CountRow>("""
            SELECT 'duplicate_customer_emails'::text AS "Name",count(*) AS "Count" FROM
              (SELECT lower(btrim(email)) FROM public.customers WHERE nullif(btrim(email),'') IS NOT NULL GROUP BY lower(btrim(email)) HAVING count(*)>1) d
            UNION ALL SELECT 'service_zero_rates',count(*) FROM public.services WHERE price<=0
            UNION ALL SELECT 'admin_non_aspnet_hashes',count(*) FROM public.admin_users WHERE password_hash !~ '^AQAAAA'
            UNION ALL SELECT 'admin_bcrypt_hashes',count(*) FROM public.admin_users WHERE left(password_hash,3) IN ('$2a','$2b','$2y')
            """).ToListAsync();
        var triggers=await db.Database.SqlQueryRaw<TriggerRow>("""
            SELECT c.relname AS "Table",t.tgname AS "Name",pg_get_triggerdef(t.oid) AS "Definition",pg_get_functiondef(t.tgfoid) AS "Function"
            FROM pg_trigger t JOIN pg_class c ON c.oid=t.tgrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND NOT t.tgisinternal ORDER BY c.relname,t.tgname
            """).ToListAsync();
        var artists=await db.Database.SqlQueryRaw<ArtistRow>("SELECT id AS \"Id\",full_name AS \"Name\",is_active AS \"Active\" FROM public.makeup_artists ORDER BY id").ToListAsync();
        await tx.CommitAsync();
        await tx.DisposeAsync();
        await connectionLease.DisposeAsync(); // Do not hold a session during report file writes.
        var path=Path.Combine(root,"App_Data","DatabaseInspection","public-data-profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new { Counts=counts,Statuses=statuses,Issues=issues,Triggers=triggers,Artists=artists },new JsonSerializerOptions { WriteIndented=true }));
        foreach(var c in counts) Console.WriteLine($"{c.Name}: {c.Count} records");
        Console.WriteLine("Aggregate-only profile saved: "+path);
    }
    public sealed class CountRow { public string Name { get; set; }=""; public long Count { get; set; } }
    public sealed class StatusCountRow { public string Table { get; set; }=""; public string Status { get; set; }=""; public long Count { get; set; } }
    public sealed class TriggerRow { public string Table { get; set; }=""; public string Name { get; set; }=""; public string Definition { get; set; }=""; public string Function { get; set; }=""; }
    public sealed class ArtistRow { public long Id { get; set; } public string Name { get; set; }=""; public bool Active { get; set; } }
}
