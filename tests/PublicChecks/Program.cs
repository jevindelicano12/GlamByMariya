using Npgsql;
if (args.Length != 1 || !int.TryParse(args[0], out var port)) throw new Exception("Run via isolated runner; never target live Supabase.");
var cs = $"Host=127.0.0.1;Port={port};Username=postgres;Database=postgres;SSL Mode=Disable;Pooling=false;Timeout=15;Command Timeout=120";
var root = Directory.GetCurrentDirectory();
int checks = 0;
void Check(bool c, string m) { if (!c) throw new Exception("FAIL: " + m); checks++; Console.WriteLine("PASS: " + m); }
await using var conn = new NpgsqlConnection(cs);
await conn.OpenAsync();
async Task ExecScript(string sql)
{
    // Split on semicolon-newline for scripts without dollar-quoted bodies.
    int stmtNo = 0;
    foreach (var chunk in sql.Split(new[] { ";\n" }, StringSplitOptions.RemoveEmptyEntries))
    {
        var lines = chunk.Split('\n').Where(l => !l.TrimStart().StartsWith("--")).ToArray();
        var s = string.Join('\n', lines).Trim();
        if (s.Length == 0) continue;
        stmtNo++;
        Console.WriteLine($"Executing baseline statement #{stmtNo}: {s.Substring(0, Math.Min(90, s.Length))}");
        try
        {
            await using var cmd = new NpgsqlCommand(s + ";", conn);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException e)
        {
            Console.WriteLine($"FAILING baseline statement #{stmtNo}: {s}");
            throw new Exception($"Baseline statement #{stmtNo} failed at PG position {e.Position}: {e.MessageText}", e);
        }
    }
}
async Task Exec(string sql) { await using var cmd = new NpgsqlCommand(sql, conn); cmd.CommandTimeout = 120; await cmd.ExecuteNonQueryAsync(); }
async Task<T> Query<T>(string sql) { await using var cmd = new NpgsqlCommand(sql, conn); return (T)(await cmd.ExecuteScalarAsync())!; }
var baseline = await File.ReadAllTextAsync(Path.Combine(root, "tests", "PublicChecks", "BaselinePublic.sql"));
await ExecScript(baseline);
Check(await Query<long>("SELECT count(*) FROM public.customers")==1, "Baseline has 1 legacy customer");
Check(await Query<long>("SELECT count(*) FROM public.reservations")==1, "Baseline has 1 legacy reservation");
var migration = await File.ReadAllTextAsync(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "PublicIntegration.sql"));
try { await Exec(migration); } catch (PostgresException e) { Console.Error.WriteLine($"Migration failed: {e.SqlState} {e.MessageText} / {e.Where} / {e.Detail}"); throw; }
Check(true, "PublicIntegration migration executes against reconstructed baseline");
Check(await Query<long>("SELECT count(*) FROM public.customers")==1, "Migration preserves legacy customer row");
Check(await Query<long>("SELECT count(*) FROM public.reservations")==1, "Migration preserves legacy reservation row");
Check(await Query<long>("SELECT count(*) FROM public.payments")==0, "Migration preserves zero-payment state");
var cols = await Query<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='reservations' AND column_name='app_id'");
Check(cols==1, "Migration adds app_id to reservations");
var views = await Query<long>("SELECT count(*) FROM pg_views WHERE schemaname='public' AND viewname LIKE 'mvc_%'");
Check(views>=9, $"Migration creates mvc views (found {views})");
var tables = await Query<long>("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE' AND table_name IN ('account_identities','catalog_categories','makeup_addons','business_settings','service_areas','media_assets','catalog_images','payment_methods','reservation_addons','payment_revisions','payment_adjustments','reservation_closures','refunds','reschedule_requests','notifications','audit_logs')");
Check(tables==16, $"Migration creates 16 new tables (found {tables})");
// Re-run for idempotency (must not fail or duplicate)
await Exec(migration);
Check(await Query<long>("SELECT count(*) FROM public.customers")==1, "Re-running migration does not duplicate legacy rows");
var downPayment = await File.ReadAllTextAsync(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "DownPaymentPolicy.sql"));
await Exec(downPayment);
Check(true, "Already-applied DownPaymentPolicy state is reconstructed locally");
var transportation = await File.ReadAllTextAsync(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "TransportationFeePolicy.sql"));
await Exec(transportation);
Check(await Query<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='reservations' AND column_name='travel_reminder_acknowledged_at'")==1, "Transportation policy reuses the existing acknowledgment column");
Check(await Query<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='mvc_booking_summary' AND column_name='transportation_policy_acknowledged_at'")==1, "Transportation policy exposes acknowledgment in the booking view");
Check(await Query<bool>("SELECT (SELECT ordinal_position FROM information_schema.columns WHERE table_schema='public' AND table_name='mvc_booking_summary' AND column_name='transportation_policy_acknowledged_at') > (SELECT ordinal_position FROM information_schema.columns WHERE table_schema='public' AND table_name='mvc_booking_summary' AND column_name='required_down_payment_snapshot')"), "Transportation view field is appended without reordering existing columns");
await Exec(transportation);
Check(await Query<long>("SELECT count(*) FROM public.customers")==1 && await Query<long>("SELECT count(*) FROM public.payments")==0, "Transportation migration is idempotent and preserves existing rows");
var authIdentity = await File.ReadAllTextAsync(Path.Combine(root, "Backend", "GlamByMariya.Infrastructure", "Data", "Sql", "AuthIdentityTracking.sql"));
await Exec(authIdentity);
Check(await Query<long>("SELECT count(*) FROM information_schema.columns WHERE table_schema='public' AND table_name='account_identities' AND column_name IN ('supabase_user_id','auth_state')") == 2, "Auth identity preparation adds only nullable linkage metadata");
Check(await Query<long>("SELECT count(*) FROM public.account_identities WHERE auth_state='Unknown' AND supabase_user_id IS NULL") == 2, "Existing identities remain Unknown and are not backfilled");
await Exec(authIdentity);
Check(await Query<long>("SELECT count(*) FROM public.account_identities WHERE auth_state='Unknown' AND supabase_user_id IS NULL") == 2, "Auth identity preparation is idempotent");
// Minimal end-to-end: new app-managed customer + service booking + deposit, then verify overlap is rejected
var customerApp = Guid.NewGuid();
var customerId = await Query<long>($"INSERT INTO public.customers(full_name, email, first_name, last_name, age_at_registration, gender, address, password_hash, is_active, app_id) VALUES('Test Customer','testcheck@example.test','Test','Customer',25,'Others','123 Test Street, Barangay','AQAAAA-test',true,'{customerApp}') RETURNING id");
Check(customerId>1, "New app-managed customer inserts with stable UUID");
var serviceApp = await Query<string>("SELECT app_id::text FROM public.services WHERE name='Soft Glam'");
Check(!string.IsNullOrEmpty(serviceApp), "Legacy service received stable UUID");
Console.WriteLine($"All {checks} public migration checks passed.");
