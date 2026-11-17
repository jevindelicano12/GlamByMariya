using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GlamByMariya.Core.BusinessRules;
using GlamByMariya.Core.Entities;
using Npgsql;
using NpgsqlTypes;

// Explicitly constructed, isolated test host only. No MVC/production DI wiring.
// This is a booking-snapshot foundation, not payment submission or completion.
internal sealed class BookingTestCreateRepository
{
    private readonly SyntheticCustomerConnections connections;
    internal BookingTestCreateRepository(SyntheticCustomerConnections connections) =>
        this.connections = connections ?? throw new ArgumentNullException(nameof(connections));

    internal static byte[] ValidateAndHash(Guid actor, BookingTestCreateRequest request)
    {
        if (request == null || actor == Guid.Empty || request.SubmissionId == Guid.Empty || request.ServiceId == Guid.Empty ||
            request.AddonIds == null || request.AddonIds.Any(x => x == Guid.Empty) || request.AddonIds.Length > 400 ||
            request.People is < 1 or > 20 || request.Package is not ("auto" or "group") ||
            (request.Package == "auto" && request.People != 1) || request.StartUtc.Kind != DateTimeKind.Utc ||
            request.StartUtc.Ticks % TimeSpan.TicksPerMinute != 0 || request.StartUtc > DateTime.SpecifyKind(DateTime.MaxValue.AddDays(-2), DateTimeKind.Utc) || string.IsNullOrWhiteSpace(request.Address) ||
            request.Address.Trim().Length is < 8 or > 500 || string.IsNullOrWhiteSpace(request.Area) || request.Area.Trim().Length > 100 ||
            !request.PolicyAccepted || !request.TransportationAccepted || request.ExpectedTotal <= 0 || request.ExpectedMinimum <= 0 ||
            request.ExpectedMinimum > request.ExpectedTotal ||
            Money.Round(request.ExpectedTotal) != request.ExpectedTotal || Money.Round(request.ExpectedMinimum) != request.ExpectedMinimum ||
            request.AddonIds.GroupBy(x => x).Any(g => g.Count() > request.People))
            throw new InvalidOperationException("Invalid isolated booking request.");
        // Canonical request fingerprint binds the replay key to the actor and all
        // meaningful client input, including quantities, without logging values.
        var canonical = JsonSerializer.Serialize(new { actor, request.SubmissionId, request.ServiceId,
            Addons = request.AddonIds.Order().ToArray(), request.People, request.Package, request.StartUtc,
            Address = request.Address.Trim(), Area = request.Area.Trim(),
            ExpectedTotal = request.ExpectedTotal.ToString("F2", CultureInfo.InvariantCulture),
            ExpectedMinimum = request.ExpectedMinimum.ToString("F2", CultureInfo.InvariantCulture),
            request.PolicyAccepted, request.TransportationAccepted });
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    internal static BookingQuote ValidateAndQuote(BookingTestCreateRequest request, CatalogItem service, IReadOnlyList<CatalogItem> addons, int percent)
    {
        if (service.Id != request.ServiceId || !service.Active || service.Archived ||
            addons.Count != request.AddonIds.Length || addons.Any(a => !a.Active || a.Archived) ||
            !addons.Select(a => a.Id).Order().SequenceEqual(request.AddonIds.Order()))
            throw new InvalidOperationException("Catalog selection is unavailable.");
        var quote = BookingPricing.Calculate(service, addons, request.People, 0, request.Package, percent);
        if (quote.FinalTotal != request.ExpectedTotal || quote.RequiredDownPayment != request.ExpectedMinimum)
            throw new InvalidOperationException("The booking quote changed.");
        return quote;
    }

    internal static bool SameReplay(byte[] existingHash, byte[] requestHash) =>
        CryptographicOperations.FixedTimeEquals(existingHash, requestHash);

    internal async Task<Guid> CreateAsync(SyntheticBookingSessions.VerifiedCustomer customer, BookingTestCreateRequest request, CancellationToken ct = default)
    {
        var actor = customer?.CustomerId ?? throw new InvalidOperationException("Verified synthetic Customer required.");
        BookingTestProtectedBoundary.RequireImplemented();
        using var operation = await customer.AcquireOperationAsync(ct);
        if (customer.CustomerId != actor) throw new InvalidOperationException("Synthetic identity changed.");
        var hash = ValidateAndHash(actor, request);
        try { return await CreateOnceAsync(customer, request, hash, ct); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ExclusionViolation)
        {
            // The other transaction has committed by the time PostgreSQL reports
            // the constraint conflict. Re-read only under the same actor and key.
            try
            {
                var replay = await FindReplayAsync(customer, request.SubmissionId, hash, ct);
                return replay ?? throw new InvalidOperationException("Booking submission conflicts with an existing reservation.");
            }
            catch (NpgsqlException) { throw new InvalidOperationException("The isolated booking test database is unavailable or incompatible."); }
        }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.CheckViolation)
        { throw new InvalidOperationException("Booking references or details are no longer valid."); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        { throw new InvalidOperationException("Booking could not be completed due to a concurrent change; review the request before retrying."); }
        catch (NpgsqlException) { throw new InvalidOperationException("The isolated booking test database is unavailable or incompatible."); }
    }

    private async Task<NpgsqlConnection> OpenGuardedAsync(SyntheticBookingSessions.VerifiedCustomer customer, CancellationToken ct)
    {
        var login = connections.ExpectedLogin(customer);
        var connection = connections.Create(customer);
        try
        {
            await connection.OpenAsync(ct);
            await using var guard = new NpgsqlCommand("""
                SELECT current_database()='glam_auth_mvc_workflow_test' AND current_user=@login AND session_user=@login
                  AND EXISTS(SELECT 1 FROM pg_roles WHERE rolname=@login AND rolcanlogin AND NOT rolsuper AND NOT rolbypassrls)
                  AND pg_has_role(current_user,'booking_test_runtime','USAGE')
                  AND booking_test.authenticated_fixture_customer()=@actor
                  AND (SELECT count(*)=2 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                       WHERE n.nspname='booking_test' AND c.relname IN ('bookings','booking_addons')
                         AND c.relrowsecurity AND c.relforcerowsecurity AND NOT pg_has_role(current_user,c.relowner,'MEMBER'))
                  AND inet_server_addr()='127.0.0.1'::inet AND inet_client_addr()='127.0.0.1'::inet
                  AND inet_server_port()=5432
                  AND EXISTS(SELECT 1 FROM public.auth_mvc_test_sentinel WHERE name='glam_auth_mvc_workflow_test')
                  AND to_regclass('booking_test.bookings') IS NOT NULL
                  AND to_regclass('booking_test.booking_addons') IS NOT NULL
                  AND to_regclass('booking_test.services') IS NOT NULL
                  AND to_regclass('booking_test.addons') IS NOT NULL
                  AND to_regclass('booking_test.settings') IS NOT NULL
                  AND to_regclass('booking_test.service_areas') IS NOT NULL
                  AND to_regclass('booking_test.schedule_blocks') IS NOT NULL
                  AND to_regclass('booking_test.synthetic_customers') IS NOT NULL
                  AND EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('booking_test.bookings') AND conname='booking_test_no_overlap' AND contype='x')
                  AND EXISTS(SELECT 1 FROM pg_constraint WHERE conrelid=to_regclass('booking_test.bookings') AND contype='u' AND pg_get_constraintdef(oid)='UNIQUE (customer_id, submission_id)')
                  AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid=to_regclass('booking_test.bookings') AND tgname='protect_booking_snapshot' AND tgenabled<>'D')
                  AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid=to_regclass('booking_test.booking_addons') AND tgname='protect_addon_snapshot' AND tgenabled<>'D')
                  AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid=to_regclass('booking_test.booking_addons') AND tgname='guard_addon_insert' AND tgenabled<>'D')
                  AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid=to_regclass('booking_test.bookings') AND tgname='require_sealed_booking' AND tgenabled<>'D')
                  AND EXISTS(SELECT 1 FROM pg_trigger WHERE tgrelid=to_regclass('booking_test.schedule_blocks') AND tgname='reject_block_writes' AND tgenabled<>'D')
                """, connection);
            guard.Parameters.AddWithValue("login", login);
            guard.Parameters.AddWithValue("actor", customer.CustomerId);
            if ((bool?)await guard.ExecuteScalarAsync(ct) != true) throw new InvalidOperationException("Isolated booking schema unavailable or unsafe.");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    internal static async Task EnsureCustomerAsync(NpgsqlConnection c, NpgsqlTransaction tx, SyntheticBookingSessions.VerifiedCustomer customer, CancellationToken ct)
    {
        BookingTestProtectedBoundary.RequireImplemented();
        var actor = customer.CustomerId;
        await using var cmd = new NpgsqlCommand("""
            SELECT id FROM booking_test.synthetic_customers WHERE id=@actor AND active
            """, c, tx);
        cmd.Parameters.AddWithValue("actor", actor);
        if (await cmd.ExecuteScalarAsync(ct) is not Guid || customer.CustomerId != actor)
            throw new InvalidOperationException("Customer account is unavailable.");
    }

    private static async Task<Guid?> ReplayAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid actor, Guid submission, byte[] hash, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT id,request_hash,snapshot_sealed FROM booking_test.bookings WHERE customer_id=@actor AND submission_id=@submission", c, tx);
        cmd.Parameters.AddWithValue("actor", actor); cmd.Parameters.AddWithValue("submission", submission);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (!reader.GetBoolean(2)) throw new InvalidOperationException("Existing booking snapshot is incomplete.");
        if (!SameReplay(reader.GetFieldValue<byte[]>(1), hash)) throw new InvalidOperationException("Submission key was already used for different booking details.");
        return reader.GetGuid(0);
    }

    private async Task<Guid?> FindReplayAsync(SyntheticBookingSessions.VerifiedCustomer customer, Guid submission, byte[] hash, CancellationToken ct)
    {
        await using var c = await OpenGuardedAsync(customer, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await BookingTestDatabaseSessionRepository.LockAsync(c, tx, customer, ct);
        await EnsureCustomerAsync(c, tx, customer, ct);
        var result = await ReplayAsync(c, tx, customer.CustomerId, submission, hash, ct);
        await BookingTestDatabaseSessionRepository.LockAsync(c, tx, customer, ct);
        _ = customer.CustomerId;
        return result;
    }

    private async Task<Guid> CreateOnceAsync(SyntheticBookingSessions.VerifiedCustomer customer, BookingTestCreateRequest r, byte[] hash, CancellationToken ct)
    {
        var actor = customer.CustomerId;
        await using var c = await OpenGuardedAsync(customer, ct);
        await using var tx = await c.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await BookingTestDatabaseSessionRepository.LockAsync(c, tx, customer, ct);
        await EnsureCustomerAsync(c, tx, customer, ct);
        var existing = await ReplayAsync(c, tx, actor, r.SubmissionId, hash, ct);
        if (existing.HasValue) { await BookingTestDatabaseSessionRepository.LockAsync(c, tx, customer, ct); _ = customer.CustomerId; return existing.Value; }
        int buffer, percent, open, close; string zone; Guid artist;
        // All booking writers must lock this same settings row; a future block
        // writer must do likewise before insertion. Exclusion remains the final
        // database guard for overlapping booking rows.
        await using (var lockSettings = new NpgsqlCommand("SELECT id FROM booking_test.settings WHERE id=1 FOR UPDATE", c, tx))
            if (await lockSettings.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Booking settings unavailable.");
        // A competing identical submission may have committed while we waited.
        // At READ COMMITTED this new statement sees it before any re-quote.
        existing = await ReplayAsync(c, tx, actor, r.SubmissionId, hash, ct);
        if (existing.HasValue) { await BookingTestDatabaseSessionRepository.LockAsync(c, tx, customer, ct); _ = customer.CustomerId; return existing.Value; }
        await using (var cmd = new NpgsqlCommand("SELECT buffer_minutes,down_payment_percent,open_hour,close_hour,time_zone,artist_id,early_fee_enabled,late_fee_enabled FROM booking_test.settings WHERE id=1", c, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Booking settings unavailable.");
            buffer=reader.GetInt32(0); percent=reader.GetInt32(1); open=reader.GetInt32(2); close=reader.GetInt32(3); zone=reader.GetString(4); artist=reader.GetGuid(5);
            if (artist==Guid.Empty || reader.GetBoolean(6) || reader.GetBoolean(7)) throw new InvalidOperationException("Fee-enabled scheduling is not implemented in this fixture.");
        }
        CatalogItem service;
        await using (var cmd = new NpgsqlCommand("SELECT name,price,minutes,group_price,group_minimum FROM booking_test.services WHERE id=@id AND active FOR SHARE", c, tx))
        {
            cmd.Parameters.AddWithValue("id",r.ServiceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Choose an active service.");
            service = new() { Id=r.ServiceId, Name=reader.GetString(0), Price=reader.GetDecimal(1), Minutes=reader.GetInt32(2),
                GroupPrice=reader.IsDBNull(3) ? null : reader.GetDecimal(3), GroupMinimumPeople=reader.GetInt32(4) };
        }
        var addons = new List<CatalogItem>();
        foreach (var group in r.AddonIds.GroupBy(x => x).OrderBy(x => x.Key))
        {
            await using var cmd = new NpgsqlCommand("SELECT name,price,minutes FROM booking_test.addons WHERE id=@id AND active FOR SHARE", c, tx);
            cmd.Parameters.AddWithValue("id", group.Key);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Choose an active add-on.");
            var addon = new CatalogItem { Id=group.Key, Name=reader.GetString(0), Price=reader.GetDecimal(1), Minutes=reader.GetInt32(2) };
            addons.AddRange(Enumerable.Repeat(addon, group.Count()));
        }
        await using (var area = new NpgsqlCommand("SELECT name FROM booking_test.service_areas WHERE name=@name AND active FOR SHARE", c, tx))
        {
            area.Parameters.AddWithValue("name", r.Area.Trim());
            if (await area.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Choose an active service area.");
        }
        var quote = ValidateAndQuote(r, service, addons, percent);
        TimeZoneInfo tz;
        try { tz=TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new InvalidOperationException("Invalid test time zone."); }
        var local = TimeZoneInfo.ConvertTimeFromUtc(r.StartUtc, tz);
        var end = r.StartUtc.AddMinutes(quote.DurationMinutes); var reserved = end.AddMinutes(buffer);
        var localReserved = TimeZoneInfo.ConvertTimeFromUtc(reserved, tz);
        if (r.StartUtc <= DateTime.UtcNow || local.TimeOfDay < TimeSpan.FromHours(open) ||
            localReserved > local.Date.AddHours(close)) throw new InvalidOperationException("Appointment does not fit the available business hours.");
        await using (var block = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM booking_test.schedule_blocks WHERE artist_id=@artist AND start_at<@reserved AND end_at>@start)",c,tx))
        {
            block.Parameters.AddWithValue("artist",artist); block.Parameters.AddWithValue("start",r.StartUtc); block.Parameters.AddWithValue("reserved",reserved);
            if ((bool?)await block.ExecuteScalarAsync(ct) == true) throw new InvalidOperationException("The artist is unavailable at this time.");
        }
        var id = Guid.NewGuid();
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO booking_test.bookings(id,submission_id,customer_id,artist_id,service_id,request_hash,
              service_name,service_price,service_minutes,group_price,group_minimum,people,package,applied_price,address,area,
              policy_accepted_at,transportation_accepted_at,original_total,discount,extras_total,agreed_total,down_percent,minimum_due,
              start_at,end_at,reserved_until,buffer_minutes,status)
            VALUES(@id,@submission,@actor,@artist,@service,@hash,@name,@price,@minutes,@groupPrice,@groupMinimum,@people,@package,@applied,@address,@area,
              now(),now(),@original,@discount,@extras,@total,@percent,@minimum,@start,@end,@reserved,@buffer,'Pending Payment')
            """, c, tx))
        {
            cmd.Parameters.AddWithValue("id",id); cmd.Parameters.AddWithValue("submission",r.SubmissionId); cmd.Parameters.AddWithValue("actor",actor);
            cmd.Parameters.AddWithValue("artist",artist); cmd.Parameters.AddWithValue("service",service.Id); cmd.Parameters.AddWithValue("hash",hash);
            cmd.Parameters.AddWithValue("name",service.Name); cmd.Parameters.AddWithValue("price",service.Price); cmd.Parameters.AddWithValue("minutes",service.Minutes);
            cmd.Parameters.Add("groupPrice",NpgsqlDbType.Numeric).Value=(object?)service.GroupPrice ?? DBNull.Value;
            cmd.Parameters.AddWithValue("groupMinimum",service.GroupMinimumPeople); cmd.Parameters.AddWithValue("people",r.People); cmd.Parameters.AddWithValue("package",r.Package);
            cmd.Parameters.AddWithValue("applied",quote.AppliedPricePerPerson); cmd.Parameters.AddWithValue("address",r.Address.Trim()); cmd.Parameters.AddWithValue("area",r.Area.Trim());
            cmd.Parameters.AddWithValue("original",quote.OriginalTotal); cmd.Parameters.AddWithValue("discount",quote.GroupDiscount); cmd.Parameters.AddWithValue("extras",quote.ExtrasTotal);
            cmd.Parameters.AddWithValue("total",quote.FinalTotal); cmd.Parameters.AddWithValue("percent",percent); cmd.Parameters.AddWithValue("minimum",quote.RequiredDownPayment);
            cmd.Parameters.AddWithValue("start",r.StartUtc); cmd.Parameters.AddWithValue("end",end); cmd.Parameters.AddWithValue("reserved",reserved); cmd.Parameters.AddWithValue("buffer",buffer);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var group in addons.GroupBy(a=>a.Id))
        {
            var a=group.First();
            await using var cmd = new NpgsqlCommand("INSERT INTO booking_test.booking_addons(booking_id,addon_id,name_snapshot,price_snapshot,minutes_snapshot,quantity) VALUES(@booking,@addon,@name,@price,@minutes,@quantity)",c,tx);
            cmd.Parameters.AddWithValue("booking",id); cmd.Parameters.AddWithValue("addon",a.Id); cmd.Parameters.AddWithValue("name",a.Name);
            cmd.Parameters.AddWithValue("price",a.Price); cmd.Parameters.AddWithValue("minutes",a.Minutes); cmd.Parameters.AddWithValue("quantity",group.Count());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var seal = new NpgsqlCommand("UPDATE booking_test.bookings SET snapshot_sealed=true WHERE id=@id AND customer_id=@actor AND NOT snapshot_sealed",c,tx))
        {
            seal.Parameters.AddWithValue("id",id); seal.Parameters.AddWithValue("actor",actor);
            if (await seal.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Booking snapshot could not be sealed.");
        }
        // Recheck expiry at commit admission while the session row lock is held.
        await BookingTestDatabaseSessionRepository.LockAsync(c, tx, customer, ct);
        _ = customer.CustomerId;
        try { await tx.CommitAsync(ct); }
        catch (Exception e) when (e is NpgsqlException or OperationCanceledException or System.IO.IOException or TimeoutException)
        {
            // Never submit a new key after an uncertain COMMIT. Retry exactly the
            // same request/key with fresh authorization; replay resolves a commit.
            throw new BookingTestCommitUnknownException();
        }
        return id;
    }
}

// No customer ID or role exists in the request body.
internal sealed record BookingTestCreateRequest(Guid SubmissionId, Guid ServiceId, Guid[] AddonIds,
    int People, string Package, DateTime StartUtc, string Address, string Area, decimal ExpectedTotal,
    decimal ExpectedMinimum, bool PolicyAccepted, bool TransportationAccepted);

internal sealed class BookingTestCommitUnknownException() : Exception(
    "Commit outcome unknown. Reauthenticate and retry the identical request with the same submission ID; do not use a new ID.");
