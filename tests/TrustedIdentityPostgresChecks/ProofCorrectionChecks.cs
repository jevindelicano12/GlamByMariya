using GlamByMariya.Infrastructure.Services;
using Npgsql;

// Synthetic fixture only. The command does not verify a real provider or object store.
internal static class ProofCorrectionChecks
{
    internal sealed record HttpFixture(long ReservationId, Guid AssetId);
    private sealed record Fixture(long ReservationId, long PaymentId, Guid AssetId, Guid InvalidAssetId);

    internal static async Task<HttpFixture> RunAsync(NpgsqlDataSource monitor, NpgsqlDataSource admin, NpgsqlDataSource backend,
        TrustedActorSession customer, TrustedActorSession other, TrustedActorSession race, TrustedActorSession http,
        Action<bool, string> check)
    {
        var first = await SeedAsync(admin, customer, "proof-a", 1);
        var second = await SeedAsync(admin, other, "proof-b", 2);
        var racing = await SeedAsync(admin, race, "proof-race", 3);
        var httpFixture = await SeedAsync(admin, http, "proof-http", 4);

        await ProofStagingChecks.RunAsync(admin, backend, customer, other, first.ReservationId, check);
        check(!await CorrectAsync(backend, customer, first, null, first.AssetId), "missing expected revision denied");
        check(!await CorrectAsync(backend, customer, first, -1, first.AssetId), "negative expected revision denied");
        check(!await CorrectAsync(backend, customer, first, 1, first.AssetId), "stale expected revision denied");
        check(!await CorrectAsync(backend, other, first, 0, first.AssetId), "cross-Customer reservation denied");
        check(!await CorrectAsync(backend, customer, first, 0, second.AssetId), "another Customer proof asset denied");
        check(!await CorrectAsync(backend, customer, first, 0, first.InvalidAssetId), "unvalidated proof asset denied");
        check(!await CorrectAsync(backend, customer with { ProviderSubject = Guid.NewGuid() }, first, 0, first.AssetId),
            "mismatched provider subject denied");
        check(!await CorrectAsync(backend, customer with { Role = "Admin" }, first, 0, first.AssetId),
            "forged staff role denied for Customer correction");
        check(await CorrectAsync(backend, customer, first, 0, first.AssetId), "owner appends correction at current revision");
        check(!await CorrectAsync(backend, customer, first, 0, first.AssetId), "replayed correction denied");
        check(await RevisionCountAsync(admin, first.PaymentId) == 2, "correction preserves the original immutable revision");

        await using (var connection = await backend.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            check(await CorrectAsync(connection, transaction, customer, first, 1, first.AssetId), "correction can be staged inside a transaction");
            await transaction.RollbackAsync();
        }
        check(await RevisionCountAsync(admin, first.PaymentId) == 2, "rollback removes only the staged correction");

        var simultaneous = await Task.WhenAll(
            CorrectAsync(backend, customer, first, 1, first.AssetId),
            CorrectAsync(backend, customer, first, 1, first.AssetId));
        check(simultaneous.Count(result => result) == 1 && await RevisionCountAsync(admin, first.PaymentId) == 3,
            "two concurrent corrections produce one new revision");
        await ConfirmSlotHeldAsync(admin, first.ReservationId, check);
        await ConfirmImmutableAsync(admin, first.PaymentId, check);

        await using (var connection = await admin.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("""
            INSERT INTO public.payment_revisions(payment_id,revision,reference,proof_asset_id,verification_status)
            VALUES(@payment,3,'SYNTHETIC-VERIFIED',@asset,'Verified')
            """, connection))
        {
            command.Parameters.AddWithValue("payment", first.PaymentId);
            command.Parameters.AddWithValue("asset", first.AssetId);
            await command.ExecuteNonQueryAsync();
        }
        check(!await CorrectAsync(backend, customer, first, 3, first.AssetId), "verified evidence cannot be corrected");
        await AssertCorrectionRevocationRaceAsync(monitor, backend, racing, race, check);
        check(!await CorrectAsync(backend, race, racing, 1, racing.AssetId), "revoked identity cannot correct again");
        await AssertRevocationFirstAsync(monitor, backend, racing, race with { SecurityGeneration = race.SecurityGeneration + 1 }, check);
        check(await RevisionCountAsync(admin, racing.PaymentId) == 2, "revocation-first denial leaves proof history unchanged");
        await SetActiveAsync(admin, other.AccountId, false);
        check(!await CorrectAsync(backend, other, second, 0, second.AssetId), "inactive Customer denied");
        await SetActiveAsync(admin, other.AccountId, true);
        return new(httpFixture.ReservationId, httpFixture.AssetId);
    }

    private static async Task<Fixture> SeedAsync(NpgsqlDataSource source, TrustedActorSession actor, string name, int day)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("""
            WITH artist AS (INSERT INTO public.makeup_artists(full_name) VALUES(@name) RETURNING id),
                 service AS (INSERT INTO public.services(name,price,duration_minutes) VALUES(@name,1000,60) RETURNING id),
                 booking AS (
                   INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
                   SELECT @name,c.id,service.id,artist.id,@start,@end,@reserved,1000
                   FROM public.customers c CROSS JOIN service CROSS JOIN artist WHERE c.app_id=@actor RETURNING id,customer_id
                 ),
                 payment AS (INSERT INTO public.payments(reservation_id) SELECT id FROM booking RETURNING id,reservation_id),
                 assets AS (
                   INSERT INTO public.payment_proof_assets(id,reservation_id,customer_id,is_validated)
                   SELECT x.id,booking.id,booking.customer_id,x.validated FROM booking
                   CROSS JOIN (VALUES(@asset,true),(@invalid,false)) x(id,validated) RETURNING id
                 )
            SELECT booking.id,payment.id FROM booking JOIN payment ON payment.reservation_id=booking.id
            """, connection, tx);
        var valid = Guid.NewGuid(); var invalid = Guid.NewGuid();
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("actor", actor.AccountId);
        cmd.Parameters.AddWithValue("start", new DateTime(2035, 1, day, 9, 0, 0, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("end", new DateTime(2035, 1, day, 10, 0, 0, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("reserved", new DateTime(2035, 1, day, 10, 45, 0, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue("asset", valid);
        cmd.Parameters.AddWithValue("invalid", invalid);
        long reservation, payment;
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Synthetic payment fixture not inserted.");
            reservation = reader.GetInt64(0); payment = reader.GetInt64(1);
        }
        await using var revision = new NpgsqlCommand("""
            INSERT INTO public.payment_revisions(payment_id,revision,reference,proof_asset_id,verification_status)
            VALUES(@payment,0,'SYNTHETIC-INITIAL',@asset,'Pending Verification')
            """, connection, tx);
        revision.Parameters.AddWithValue("payment", payment);
        revision.Parameters.AddWithValue("asset", valid);
        await revision.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return new(reservation, payment, valid, invalid);
    }

    private static async Task<bool> CorrectAsync(NpgsqlDataSource source, TrustedActorSession actor, Fixture fixture, long? revision, Guid asset)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var tx = await connection.BeginTransactionAsync();
        var result = await CorrectAsync(connection, tx, actor, fixture, revision, asset);
        await tx.CommitAsync();
        return result;
    }

    private static async Task<bool> CorrectAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, TrustedActorSession actor, Fixture fixture, long? revision, Guid asset)
    {
        await using var command = new NpgsqlCommand("""
            SELECT v2_authz_api.fixture_correct_customer_proof(@account,@subject,@role,@epoch,@generation,@reservation,@revision,@reference,@asset)
            """, connection, tx);
        command.Parameters.AddWithValue("account", actor.AccountId);
        command.Parameters.AddWithValue("subject", actor.ProviderSubject);
        command.Parameters.AddWithValue("role", actor.Role);
        command.Parameters.AddWithValue("epoch", actor.CredentialEpoch);
        command.Parameters.AddWithValue("generation", actor.SecurityGeneration);
        command.Parameters.AddWithValue("reservation", fixture.ReservationId);
        command.Parameters.AddWithValue("revision", NpgsqlTypes.NpgsqlDbType.Bigint, (object?)revision ?? DBNull.Value);
        command.Parameters.AddWithValue("reference", "SYNTHETIC-CORRECTION");
        command.Parameters.AddWithValue("asset", asset);
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<long> RevisionCountAsync(NpgsqlDataSource source, long paymentId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM public.payment_revisions WHERE payment_id=@id", connection);
        cmd.Parameters.AddWithValue("id", paymentId);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ConfirmSlotHeldAsync(NpgsqlDataSource source, long reservation, Action<bool, string> check)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var status = new NpgsqlCommand("SELECT status FROM public.reservations WHERE id=@id", connection);
        status.Parameters.AddWithValue("id", reservation);
        check((string?)await status.ExecuteScalarAsync() == "Pending Payment", "correction leaves reservation status unchanged");
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO public.reservations(reference,customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price)
                SELECT 'synthetic-conflict',customer_id,service_id,artist_id,start_at,end_at,reserved_until,total_price
                FROM public.reservations WHERE id=@id
                """, connection);
            insert.Parameters.AddWithValue("id", reservation);
            await insert.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Overlapping reservation unexpectedly accepted.");
        }
        catch (PostgresException e) when (e.SqlState == "23P01") { check(true, "Pending Payment slot remains protected"); }
    }

    private static async Task ConfirmImmutableAsync(NpgsqlDataSource source, long payment, Action<bool, string> check)
    {
        await using var connection = await source.OpenConnectionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand("UPDATE public.payment_revisions SET reference='MUTATED' WHERE payment_id=@id", connection);
            cmd.Parameters.AddWithValue("id", payment);
            await cmd.ExecuteNonQueryAsync();
            throw new InvalidOperationException("Synthetic history unexpectedly mutable.");
        }
        catch (PostgresException e) when (e.MessageText == "Synthetic history is append-only")
        { check(true, "synthetic history rejects updates"); }
    }

    private static async Task SetActiveAsync(NpgsqlDataSource source, Guid account, bool active)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("UPDATE public.customers SET is_active=@active WHERE app_id=@id", connection);
        cmd.Parameters.AddWithValue("active", active); cmd.Parameters.AddWithValue("id", account);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task AssertCorrectionRevocationRaceAsync(NpgsqlDataSource monitor, NpgsqlDataSource backend, Fixture fixture,
        TrustedActorSession actor, Action<bool, string> check)
    {
        await using var writer = await backend.OpenConnectionAsync();
        await using var tx = await writer.BeginTransactionAsync();
        check(await CorrectAsync(writer, tx, actor, fixture, 0, fixture.AssetId), "correction acquired identity locks in transaction");
        await using var revoker = await backend.OpenConnectionAsync();
        await using var revoke = new NpgsqlCommand("SELECT v2_authz_api.revoke_my_sessions(@account,@subject,@role,@epoch,@generation)", revoker);
        revoke.Parameters.AddWithValue("account", actor.AccountId); revoke.Parameters.AddWithValue("subject", actor.ProviderSubject);
        revoke.Parameters.AddWithValue("role", actor.Role); revoke.Parameters.AddWithValue("epoch", actor.CredentialEpoch);
        revoke.Parameters.AddWithValue("generation", actor.SecurityGeneration);
        var pending = revoke.ExecuteScalarAsync();
        var blocked = false;
        try
        {
            await using var connection = await monitor.OpenConnectionAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!timeout.IsCancellationRequested)
            {
                await using var probe = new NpgsqlCommand("SELECT @blocker=ANY(pg_blocking_pids(@waiter))", connection);
                probe.Parameters.AddWithValue("blocker", writer.ProcessID); probe.Parameters.AddWithValue("waiter", revoker.ProcessID);
                if (await probe.ExecuteScalarAsync(timeout.Token) is true) { blocked = true; break; }
                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { await tx.CommitAsync(); }
        check(blocked, "revocation waits for an in-flight proof correction");
        check(await pending.WaitAsync(TimeSpan.FromSeconds(8)) is long next && next == actor.SecurityGeneration + 1,
            "revocation advances generation after correction commits");
    }

    private static async Task AssertRevocationFirstAsync(NpgsqlDataSource monitor, NpgsqlDataSource backend, Fixture fixture,
        TrustedActorSession actor, Action<bool, string> check)
    {
        await using var revoker = await backend.OpenConnectionAsync();
        await using var tx = await revoker.BeginTransactionAsync();
        await using var revoke = new NpgsqlCommand("SELECT v2_authz_api.revoke_my_sessions(@account,@subject,@role,@epoch,@generation)", revoker, tx);
        revoke.Parameters.AddWithValue("account", actor.AccountId); revoke.Parameters.AddWithValue("subject", actor.ProviderSubject);
        revoke.Parameters.AddWithValue("role", actor.Role); revoke.Parameters.AddWithValue("epoch", actor.CredentialEpoch);
        revoke.Parameters.AddWithValue("generation", actor.SecurityGeneration);
        check(await revoke.ExecuteScalarAsync() is long next && next == actor.SecurityGeneration + 1, "revocation obtains generation lock before correction");
        await using var writer = await backend.OpenConnectionAsync();
        var pending = CorrectAsync(writer, null, actor, fixture, 1, fixture.AssetId);
        var blocked = false;
        try
        {
            await using var connection = await monitor.OpenConnectionAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!timeout.IsCancellationRequested)
            {
                await using var probe = new NpgsqlCommand("SELECT @blocker=ANY(pg_blocking_pids(@waiter))", connection);
                probe.Parameters.AddWithValue("blocker", revoker.ProcessID); probe.Parameters.AddWithValue("waiter", writer.ProcessID);
                if (await probe.ExecuteScalarAsync(timeout.Token) is true) { blocked = true; break; }
                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally { await tx.CommitAsync(); }
        check(blocked, "proof correction waits for in-flight revocation");
        check(!await pending.WaitAsync(TimeSpan.FromSeconds(8)), "old generation correction denied after revocation commits");
    }
}
