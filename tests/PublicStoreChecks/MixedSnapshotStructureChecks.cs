using Npgsql;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

// Only called after the runner's marker and fresh-schema checks. The reviewed
// DDL is exercised inside a transaction that is always rolled back.
internal static class MixedSnapshotStructureChecks
{
    internal static async Task RunAsync(NpgsqlConnection connection, string candidate, Action<bool,string> check,
        IHttpContextAccessor http, IWebHostEnvironment env, IConfiguration config)
    {
        const string begin = "-- BEGIN POLICY-INDEPENDENT REVIEW DDL";
        const string end = "-- END POLICY-INDEPENDENT REVIEW DDL";
        var first = candidate.IndexOf(begin, StringComparison.Ordinal);
        var last = candidate.IndexOf(end, StringComparison.Ordinal);
        if (first < 0 || last <= first || candidate.IndexOf(begin, first + begin.Length, StringComparison.Ordinal) >= 0 ||
            !candidate.Contains("RAISE EXCEPTION 'Persistent mixed foundation is review-only and incomplete'", StringComparison.Ordinal))
            throw new InvalidOperationException("Review-only SQL markers or abort guard are invalid.");
        var body = candidate[(first + begin.Length)..last];
        await using var tx = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var options = new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(connection).Options;
        using var db = new PublicMakeupDbContext(options);
        await db.Database.UseTransactionAsync(tx);
        var store = new PublicStudioStore(db, http, env, config, new LocalAppFileStore(env));
        async Task Execute(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            await cmd.ExecuteNonQueryAsync();
        }
        async Task<T> Scalar<T>(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, connection, tx);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }
        async Task Reject(string sql, string code, string name)
        {
            await tx.SaveAsync("before_rejected_statement");
            var rejected = false;
            try { await Execute(sql); }
            catch (PostgresException e) when (e.SqlState == code) { rejected = true; }
            finally { await tx.RollbackAsync("before_rejected_statement"); }
            check(rejected, name);
        }
        try
        {
            var missingDenied = false;
            try { store.EnsureMixedReadVisibility(); } catch (InvalidOperationException) { missingDenied = true; }
            check(missingDenied, "repository mixed visibility prerequisite rejects missing schema");
            await Execute(body);
            store.EnsureMixedReadVisibility();
            check(true, "repository mixed visibility prerequisite permits complete unfiltered test schema");
            await Execute("""
                CREATE ROLE mixed_visibility_test NOLOGIN;
                GRANT USAGE ON SCHEMA public TO mixed_visibility_test;
                GRANT SELECT ON public.reservations,public.customers,public.admin_users,public.account_identities,
                    public.reservation_mixed_snapshots,public.reservation_mixed_people,public.reservation_mixed_person_addons,
                    public.mixed_service_classifications,public.services,public.payments,public.payment_revisions TO mixed_visibility_test;
                SET LOCAL ROLE mixed_visibility_test
                """);
            var hiddenDenied = false;
            try { store.EnsureMixedReadVisibility(); } catch (InvalidOperationException) { hiddenDenied = true; }
            finally { await Execute("RESET ROLE"); }
            check(hiddenDenied, "repository mixed visibility prerequisite rejects RLS-filtered headers");
            await Execute("UPDATE public.services SET app_id='41c611af-c52c-446a-ae01-1233acf20f32'::uuid WHERE id=3");
            check(await Scalar<bool>("""
                SELECT count(*)=1 AND bool_and(id=3 AND app_id='41c611af-c52c-446a-ae01-1233acf20f32'::uuid AND is_active AND NOT is_archived)
                FROM public.services WHERE id=3 OR app_id='41c611af-c52c-446a-ae01-1233acf20f32'::uuid
                """), "synthetic public catalog matches the owner-reported Bridal mapping");
            await Execute("INSERT INTO public.mixed_service_classifications(service_id,is_bridal) VALUES (1,false),(2,false),(3,true)");
            var unclassified = await Scalar<long>("INSERT INTO public.services(name,price,duration_minutes) VALUES('Synthetic Unclassified',700,60) RETURNING id");
            await Execute("""
                INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,total_price,status,people)
                VALUES('MIXED-STRUCTURE',1,1,1,now()+interval '200 days',now()+interval '200 days 1 hour',1,'Cancelled',2)
                """);
            var id = await Scalar<long>("SELECT id FROM public.reservations WHERE reference='MIXED-STRUCTURE'");
            check(await Scalar<string>($"SELECT snapshot_state FROM public.reservation_mixed_state WHERE reservation_id={id}") == "legacy",
                "reservation without a mixed header is classified as legacy");
            await Reject($"INSERT INTO public.reservation_mixed_snapshots(reservation_id,sealed,service_subtotal,addon_subtotal,service_minutes,addon_minutes) VALUES({id},true,0,0,0,0)",
                PostgresErrorCodes.RaiseException, "mixed header cannot be inserted already sealed");
            await Execute($"INSERT INTO public.reservation_mixed_snapshots(reservation_id) VALUES({id})");
            check(await Scalar<string>($"SELECT snapshot_state FROM public.reservation_mixed_state WHERE reservation_id={id}") == "incomplete",
                "unsealed header is classified as incomplete, not legacy");
            check(await Scalar<long>($"SELECT count(*) FROM public.reservation_mixed_snapshots WHERE reservation_id={id}") == 1,
                "fixture contains the mixed header before restricted-role check");
            await Execute("SET LOCAL ROLE mixed_visibility_test");
            try
            {
                check(await Scalar<long>($"SELECT count(*) FROM public.reservation_mixed_snapshots WHERE reservation_id={id}") == 0,
                    "restricted role cannot see an existing header through default-deny RLS");
                check(await Scalar<string>("SELECT set_config('makeup.actor_id','11111111-1111-4111-8111-111111111111',true)") ==
                    "11111111-1111-4111-8111-111111111111",
                    "restricted role can set the actor context and it is not identity proof");
            }
            finally
            {
                await Execute("RESET ROLE");
                await Execute("SELECT set_config('makeup.actor_id','',true)");
            }
            await Execute($"INSERT INTO public.reservation_mixed_people VALUES({id},1,3,true,'Synthetic Bridal',1500,120)");
            await Reject($"INSERT INTO public.reservation_mixed_people VALUES({id},2,3,true,'Synthetic Bridal',1500,120)",
                PostgresErrorCodes.UniqueViolation, "one authoritative Bridal selection per reservation");
            await Reject($"INSERT INTO public.reservation_mixed_people VALUES({id},2,3,false,'Renamed service',1500,120)",
                PostgresErrorCodes.ForeignKeyViolation, "Bridal classification cannot be forged by changing its label");
            await Reject($"INSERT INTO public.reservation_mixed_people VALUES({id},2,{unclassified},false,'Unclassified',700,60)",
                PostgresErrorCodes.ForeignKeyViolation, "unclassified services fail closed");
            await Reject($"UPDATE public.reservation_mixed_snapshots SET sealed=true WHERE reservation_id={id}",
                PostgresErrorCodes.RaiseException, "incomplete party cannot be sealed");
            await Execute($"INSERT INTO public.reservation_mixed_people VALUES({id},2,1,false,'Synthetic Soft',600,60)");
            await Execute($"INSERT INTO public.reservation_mixed_person_addons VALUES({id},1,'50000000-0000-0000-0000-000000000001','Synthetic Lash',250,15)");
            await Reject($"INSERT INTO public.reservation_mixed_person_addons VALUES({id},1,'50000000-0000-0000-0000-000000000001','Synthetic Lash',250,15)",
                PostgresErrorCodes.UniqueViolation, "duplicate add-on for one person is rejected");
            await Reject($"UPDATE public.reservation_mixed_people SET service_name_snapshot='rewritten' WHERE reservation_id={id}",
                PostgresErrorCodes.RaiseException, "person snapshots reject updates");
            await Reject($"DELETE FROM public.reservation_mixed_person_addons WHERE reservation_id={id}",
                PostgresErrorCodes.RaiseException, "add-on snapshots reject deletes");
            await Execute($"UPDATE public.reservation_mixed_snapshots SET sealed=true WHERE reservation_id={id}");
            check(await Scalar<string>($"SELECT snapshot_state FROM public.reservation_mixed_state WHERE reservation_id={id}") == "mixed",
                "sealed header is classified as mixed");
            check(await Scalar<bool>($"""
                SELECT sealed AND service_subtotal=2100 AND addon_subtotal=250
                  AND service_minutes=180 AND addon_minutes=15
                FROM public.reservation_mixed_snapshots WHERE reservation_id={id}
                """), "sealed aggregates equal the per-person snapshots");
            check(await Scalar<bool>($"""
                SELECT count(*)=2 AND string_agg(service_name_snapshot,',' ORDER BY person_index)='Synthetic Bridal,Synthetic Soft'
                FROM public.reservation_mixed_people WHERE reservation_id={id}
                """), "ordered per-person snapshot rows reconstruct from PostgreSQL");
            var bookingId = await Scalar<Guid>($"SELECT app_id FROM public.reservations WHERE id={id}");
            var ownerId = await Scalar<Guid>("SELECT app_id FROM public.customers WHERE id=1");
            var bridalId = await Scalar<Guid>("SELECT app_id FROM public.services WHERE id=3");
            var people = new List<PreparedPersistentBooking.MixedPersonRow>();
            await using (var read = new NpgsqlCommand($"""
                SELECT p.person_index, s.app_id, p.is_bridal, p.service_name_snapshot, p.service_price_snapshot, p.service_minutes_snapshot
                FROM public.reservation_mixed_people p JOIN public.services s ON s.id=p.service_id
                WHERE p.reservation_id={id} ORDER BY p.person_index
                """, connection, tx))
            await using (var rows = await read.ExecuteReaderAsync())
                while (await rows.ReadAsync()) people.Add(new(bookingId, rows.GetInt16(0), rows.GetGuid(1), rows.GetBoolean(2),
                    rows.GetString(3), rows.GetDecimal(4), rows.GetInt32(5)));
            var addons = new List<PreparedPersistentBooking.MixedAddonRow>();
            await using (var read = new NpgsqlCommand($"""
                SELECT person_index, addon_id, name_snapshot, price_snapshot, minutes_snapshot
                FROM public.reservation_mixed_person_addons WHERE reservation_id={id}
                """, connection, tx))
            await using (var rows = await read.ExecuteReaderAsync())
                while (await rows.ReadAsync()) addons.Add(new(bookingId, rows.GetInt16(0), rows.GetGuid(1), rows.GetString(2), rows.GetDecimal(3), rows.GetInt32(4)));
            await Execute("UPDATE public.services SET name='Synthetic Renamed Bridal',price=1600,duration_minutes=130 WHERE id=3");
            var header = new PreparedPersistentBooking.MixedHeader(bookingId, ownerId, true, 2, 2100, 250, 180, 15);
            var owner = new AppUser { Id=ownerId, Role="Customer", IsActive=true };
            var reconstructed = PreparedPersistentBooking.ReconstructMixed(owner, header, bridalId, people, addons);
            check(reconstructed.Count==2 && reconstructed[0].Service.Name=="Synthetic Bridal" && reconstructed[0].Service.Price==1500
                && reconstructed[0].DurationMinutes==135 && reconstructed[0].AddOns.Single().Price==250,
                "existing domain reconstruction retains PostgreSQL snapshot values after catalog edits");
            var denied = false;
            try { PreparedPersistentBooking.ReconstructMixed(new AppUser { Id=Guid.NewGuid(), Role="Customer" }, header, bridalId, people, addons); }
            catch (InvalidOperationException) { denied=true; }
            check(denied, "reconstruction rejects another Customer using PostgreSQL snapshot rows");
            await Reject($"INSERT INTO public.reservation_mixed_person_addons VALUES({id},2,'50000000-0000-0000-0000-000000000001','Late',250,15)",
                PostgresErrorCodes.RaiseException, "sealed snapshot cannot receive more lines");
            await Execute("""
                INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,total_price,status,people)
                VALUES('MIXED-NO-BRIDAL',1,1,1,now()+interval '202 days',now()+interval '202 days 1 hour',1,'Cancelled',2)
                """);
            var noBridal = await Scalar<long>("SELECT id FROM public.reservations WHERE reference='MIXED-NO-BRIDAL'");
            await Execute($"INSERT INTO public.reservation_mixed_snapshots(reservation_id) VALUES({noBridal})");
            await Execute($"INSERT INTO public.reservation_mixed_people VALUES({noBridal},1,1,false,'Soft',600,60),({noBridal},2,2,false,'Glamour',750,90)");
            await Execute($"UPDATE public.reservation_mixed_snapshots SET sealed=true WHERE reservation_id={noBridal}");
            check(await Scalar<bool>($"SELECT sealed AND service_subtotal=1350 AND addon_subtotal=0 FROM public.reservation_mixed_snapshots WHERE reservation_id={noBridal}"),
                "mixed reservation without Bridal is allowed");
            await Execute("SET CONSTRAINTS ALL IMMEDIATE");
            check(true, "sealed snapshot satisfies deferred structural constraint");
            await Reject("""
                SET CONSTRAINTS ALL DEFERRED;
                INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,total_price,status,people)
                VALUES('MIXED-UNSEALED',1,1,1,now()+interval '201 days',now()+interval '201 days 1 hour',1,'Cancelled',2);
                INSERT INTO public.reservation_mixed_snapshots(reservation_id)
                SELECT id FROM public.reservations WHERE reference='MIXED-UNSEALED';
                SET CONSTRAINTS ALL IMMEDIATE
                """, PostgresErrorCodes.RaiseException, "unsealed snapshot cannot pass the deferred constraint");
            await Reject("UPDATE public.mixed_service_classifications SET is_bridal=false WHERE service_id=3",
                PostgresErrorCodes.RaiseException, "authoritative service classification cannot be rewritten");
        }
        finally { await tx.RollbackAsync(); }
        await using var verify = new NpgsqlCommand("""
            SELECT to_regclass('public.reservation_mixed_snapshots') IS NULL
               AND to_regclass('public.reservation_mixed_state') IS NULL
               AND to_regclass('public.reservation_mixed_people') IS NULL
               AND to_regclass('public.mixed_service_classifications') IS NULL
               AND NOT EXISTS (SELECT 1 FROM public.reservations WHERE reference IN ('MIXED-STRUCTURE','MIXED-NO-BRIDAL'))
            """, connection);
        check((bool?)await verify.ExecuteScalarAsync() == true, "all structural test DDL and synthetic rows rolled back");
    }
}
