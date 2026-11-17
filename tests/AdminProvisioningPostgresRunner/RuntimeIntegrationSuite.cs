using System.Net;
using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Npgsql;

// This suite contains the future end-to-end invocation of the actual
// PublicStudioStore methods. Program.cs cannot reach it while ExecutionArmed is
// false. It does not substitute copied confirmation SQL or an in-memory oracle.
internal static class RuntimeIntegrationSuite
{
    public static async Task<int> RunAsync(RunnerSafety.ExplicitOptions settings)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = settings.Host.ToString(),
            Port = settings.Port,
            Database = settings.Database,
            Username = settings.Username,
            Password = settings.Password,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 20,
            SslMode = SslMode.VerifyFull
        };

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await VerifyServerIdentityAsync(connection, settings);
        await VerifyFreshPublicSchemaAsync(connection);
        await BootstrapFreshSyntheticDatabaseAsync(connection);
        await RunStoreCasesAsync(connection);
        return 0;
    }

    private static async Task VerifyServerIdentityAsync(NpgsqlConnection connection, RunnerSafety.ExplicitOptions settings)
    {
        await using var command = new NpgsqlCommand("SELECT current_database(), current_user, inet_server_addr()::text, inet_server_port()", connection);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()
            || reader.GetString(0) != settings.Database
            || reader.GetString(1) != settings.Username
            || reader.GetString(2) != settings.Host.ToString()
            || reader.GetInt32(3) != settings.Port)
            throw new InvalidOperationException("Server identity did not match the explicit disposable test target.");

        // This identity query and any caller-supplied receipt are insufficient
        // to prove disposal. Execution remains source-gated until an independent
        // pinned attestation/endpoint authority exists.
    }

    private static async Task BootstrapFreshSyntheticDatabaseAsync(NpgsqlConnection connection)
    {
        var root = FindRepositoryRoot();
        await ExecuteNewTransactionAsync(connection, await File.ReadAllTextAsync(Path.Combine(root,
            "tests", "AdminProvisioningPostgresHarnessPreparation", "SyntheticFixture.sql")));
        var historicalBefore = await CaptureHistoricalAccountsAsync(connection);
        await ExecuteNewTransactionAsync(connection, await File.ReadAllTextAsync(Path.Combine(root,
            "Data", "Sql", "AuthIdentityTracking.sql")));
        await ExecuteNewTransactionAsync(connection, await File.ReadAllTextAsync(Path.Combine(root,
            "Data", "Sql", "AdminInvitationProvisioning.sql")));
        await VerifyInstalledSchemaAsync(connection);
        await VerifyHistoricalAccountsUnchangedAsync(connection, historicalBefore);
        await ExecuteNewTransactionAsync(connection, await File.ReadAllTextAsync(Path.Combine(root,
            "tests", "AdminProvisioningPostgresHarnessPreparation", "SyntheticPendingData.sql")));
        var preparedCandidate = await ReadAccountAsync(connection, Guid.Parse("f217f000-0000-4000-8000-000000000004"));
        Require(preparedCandidate.State == "Pending" && !preparedCandidate.Active && preparedCandidate.Subject is null
            && preparedCandidate.InvitationId == Guid.Parse("f217f000-0000-4000-8000-000000000101"),
            "Synthetic pending fixture correlates exactly one inactive Admin to its consumed invitation.");
    }

    private static async Task ExecuteNewTransactionAsync(NpgsqlConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<List<HistoricalAccountSnapshot>> CaptureHistoricalAccountsAsync(NpgsqlConnection connection)
    {
        const string query = """
            SELECT i.id,coalesce(c.app_id,u.app_id),coalesce(c.password_hash,u.password_hash),
                   CASE WHEN c.id IS NOT NULL THEN 'Customer' ELSE u.role END,
                   coalesce(c.is_active,u.is_active),coalesce(c.email,u.email),
                   i.customer_id,i.admin_id,i.login
            FROM public.account_identities i
            LEFT JOIN public.customers c ON c.id=i.customer_id
            LEFT JOIN public.admin_users u ON u.id=i.admin_id
            WHERE i.id IN ('f217f000-0000-4000-8000-000000000001','f217f000-0000-4000-8000-000000000002','f217f000-0000-4000-8000-000000000003')
            ORDER BY i.id
            """;
        await using var command = new NpgsqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var accounts = new List<HistoricalAccountSnapshot>();
        while (await reader.ReadAsync())
            accounts.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                reader.GetBoolean(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.GetString(8)));
        Require(accounts.Count == 3, "Capture all three historical synthetic Customer/Admin/SuperAdmin rows before migrations.");
        return accounts;
    }

    private static async Task VerifyHistoricalAccountsUnchangedAsync(NpgsqlConnection connection,
        IReadOnlyList<HistoricalAccountSnapshot> before)
    {
        var after = await CaptureHistoricalAccountsAsync(connection);
        Require(before.SequenceEqual(after), "Migration payloads preserve historical IDs, app IDs, password hashes, roles, active flags, emails and identity linkage.");
        const string query = """
            SELECT count(*)=3 AND bool_and(admin_provisioning_state='NotApplicable'
                AND admin_invitation_id IS NULL AND auth_state='Unknown' AND supabase_user_id IS NULL)
            FROM public.account_identities
            WHERE id IN ('f217f000-0000-4000-8000-000000000001','f217f000-0000-4000-8000-000000000002','f217f000-0000-4000-8000-000000000003')
            """;
        await using var command = new NpgsqlCommand(query, connection);
        Require(Equals(await command.ExecuteScalarAsync(), true), "Historical identities retain safe legacy state and no invitation/provider linkage.");
    }

    private static async Task VerifyInstalledSchemaAsync(NpgsqlConnection connection)
    {
        const string query = """
            SELECT
              EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='account_identities'
                AND column_name='admin_provisioning_state' AND data_type='character varying' AND character_maximum_length=20
                AND is_nullable='NO' AND column_default LIKE '%NotApplicable%')
              AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema='public' AND table_name='account_identities'
                AND column_name='admin_invitation_id' AND data_type='uuid' AND is_nullable='YES')
              AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid='public.account_identities'::regclass
                AND conname='account_identities_admin_invitation_fkey' AND contype='f'
                AND confrelid='public.admin_invitations'::regclass AND confdeltype='r' AND convalidated)
              AND (SELECT count(*)=4 FROM pg_constraint WHERE conrelid='public.account_identities'::regclass
                AND convalidated AND conname IN ('account_identities_auth_state_check',
                  'account_identities_admin_provisioning_state_check',
                  'account_identities_admin_provisioning_correlation_check',
                  'account_identities_admin_confirmed_link_check'))
              AND EXISTS (SELECT 1 FROM pg_index i WHERE i.indexrelid=to_regclass('public.ux_account_identities_admin_invitation')
                AND i.indisunique AND i.indisvalid AND i.indisready AND i.indpred IS NOT NULL
                AND pg_get_expr(i.indpred,i.indrelid) LIKE '%admin_invitation_id IS NOT NULL%')
              AND EXISTS (SELECT 1 FROM pg_index i WHERE i.indexrelid=to_regclass('public.ux_account_identities_supabase_user')
                AND i.indisunique AND i.indisvalid AND i.indisready AND i.indpred IS NOT NULL
                AND pg_get_expr(i.indpred,i.indrelid) LIKE '%supabase_user_id IS NOT NULL%')
            """;
        await using var command = new NpgsqlCommand(query, connection);
        Require(Equals(await command.ExecuteScalarAsync(), true),
            "Installed columns, validated checks/FK, and both partial unique indexes match the reviewed contract.");
    }

    private static async Task VerifyFreshPublicSchemaAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relkind IN ('r','p','v','m','S','f')
            """, connection);
        var relationCount = (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Could not inspect the target schema."));
        if (relationCount != 0)
            throw new InvalidOperationException("Refusing fixture setup: target public schema is not empty.");
    }

    private static async Task RunStoreCasesAsync(NpgsqlConnection connection)
    {
        var providerSubject = Guid.Parse("f217f000-0000-4000-8000-000000000201");
        var invitationId = Guid.Parse("f217f000-0000-4000-8000-000000000211");
        await InsertInvitationAsync(connection, invitationId, "runner-success@example.invalid", accepted: true);

        using var context = CreateContext(connection);
        var store = CreateStore(context);
        var admin = store.CreatePendingInvitedAdmin("Synthetic", "Runner", "runner-success@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", invitationId);
        var before = await ReadAccountAsync(connection, admin.Id);
        Require(before.State == "Pending" && !before.Active && before.Subject is null,
            "Actual store method creates an inactive synthetic Pending Admin.");

        var confirmed = SupabaseProvisioningResult.Confirmed(providerSubject);
        store.ConfirmAdminProvisioning(admin.Id, invitationId, admin.Email, confirmed);
        var after = await ReadAccountAsync(connection, admin.Id);
        Require(after.State == "Confirmed" && after.Active && after.Subject == providerSubject && after.AuthState == "Supabase",
            "Actual store confirmation links the provider subject and activates only the matching Admin.");
        store.ConfirmAdminProvisioning(admin.Id, invitationId, admin.Email, confirmed);
        Require(await ReadAccountAsync(connection, admin.Id) == after, "Exact repeat is idempotent.");

        await ExpectStoreFailureAsync(connection, store, admin.Id, invitationId, admin.Email,
            SupabaseProvisioningResult.Confirmed(Guid.NewGuid()), "Conflicting confirmed repeat is rejected.");

        var otherAccountInvite = Guid.Parse("f217f000-0000-4000-8000-000000000217");
        await InsertInvitationAsync(connection, otherAccountInvite, "runner-other-account@example.invalid", accepted: true);
        var otherAccount = store.CreatePendingInvitedAdmin("Synthetic", "OtherAccount", "runner-other-account@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", otherAccountInvite);
        await ExpectStoreFailureAsync(connection, store, otherAccount.Id, invitationId, otherAccount.Email,
            SupabaseProvisioningResult.Confirmed(Guid.NewGuid()),
            "A consumed invitation already belonging to another synthetic account is rejected.");

        var rejectedId = Guid.Parse("f217f000-0000-4000-8000-000000000202");
        await InsertInvitationAsync(connection, rejectedId, "runner-rejected@example.invalid", accepted: true);
        var rejectedAdmin = store.CreatePendingInvitedAdmin("Synthetic", "Rejected", "runner-rejected@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", rejectedId);
        await ExpectStoreFailureAsync(connection, store, rejectedAdmin.Id, rejectedId, rejectedAdmin.Email,
            SupabaseProvisioningResult.Unconfirmed(SupabaseProvisioningOutcome.Unconfirmed, SupabaseProvisioningFailure.InvalidResponse),
            "Rejected/uncertain provider result cannot activate an account.");
        Require(!(await ReadAccountAsync(connection, rejectedAdmin.Id)).Active, "Rejected provider case remains inactive.");

        var duplicateId = Guid.Parse("f217f000-0000-4000-8000-000000000203");
        await InsertInvitationAsync(connection, duplicateId, "runner-duplicate@example.invalid", accepted: true);
        var duplicateAdmin = store.CreatePendingInvitedAdmin("Synthetic", "Duplicate", "runner-duplicate@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", duplicateId);
        await ExpectStoreFailureAsync(connection, store, duplicateAdmin.Id, duplicateId, duplicateAdmin.Email, confirmed,
            "Provider subject already linked to another identity is rejected.");
        Require(!(await ReadAccountAsync(connection, duplicateAdmin.Id)).Active, "Duplicate subject failure leaves Admin inactive.");

        var duplicateInvite = Guid.Parse("f217f000-0000-4000-8000-000000000215");
        await InsertInvitationAsync(connection, duplicateInvite, "runner-invite-conflict@example.invalid", accepted: true);
        var inviteConflict = store.CreatePendingInvitedAdmin("Synthetic", "InviteConflict", "runner-invite-conflict@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", duplicateInvite);
        var inviteConflictBefore = await ReadAccountAsync(connection, inviteConflict.Id);
        var uniqueInvitationRejected = false;
        try
        {
            await ExecAsync(connection, "UPDATE public.account_identities SET admin_invitation_id=@existing WHERE id=@id",
                ("existing", invitationId), ("id", inviteConflict.Id));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            uniqueInvitationRejected = true;
        }
        Require(uniqueInvitationRejected && await ReadAccountAsync(connection, inviteConflict.Id) == inviteConflictBefore,
            "A second local identity cannot claim the already-correlated invitation; its state remains unchanged.");

        var foreignKeyBefore = await ReadAccountAsync(connection, inviteConflict.Id);
        var foreignKeyRejected = false;
        try
        {
            await ExecAsync(connection, "UPDATE public.account_identities SET admin_invitation_id=@missing WHERE id=@id",
                ("missing", Guid.Parse("f217f000-0000-4000-8000-000000000216")), ("id", inviteConflict.Id));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            foreignKeyRejected = true;
        }
        Require(foreignKeyRejected && await ReadAccountAsync(connection, inviteConflict.Id) == foreignKeyBefore,
            "Unknown invitation UUID is rejected by the prepared foreign key without mutating the identity.");

        await ExpectConstraintFailureWithoutMutationAsync(connection, inviteConflict.Id,
            "UPDATE public.account_identities SET admin_provisioning_state='InvalidState' WHERE id=@id",
            "Provisioning-state CHECK rejects an unknown state.");
        await ExpectConstraintFailureWithoutMutationAsync(connection, inviteConflict.Id,
            "UPDATE public.account_identities SET admin_provisioning_state='Pending',admin_invitation_id=NULL WHERE id=@id",
            "Correlation CHECK rejects a non-legacy state without an invitation.");
        await ExpectConstraintFailureWithoutMutationAsync(connection, inviteConflict.Id,
            "UPDATE public.account_identities SET admin_provisioning_state='Confirmed' WHERE id=@id",
            "Confirmed-link CHECK rejects a confirmed state without a provider identity.");

        await RunCorrelationFailuresAsync(connection, store);
        await RunRollbackCaseAsync(connection, store);
        await RunConcurrentCaseAsync(connection);
    }

    private static async Task RunCorrelationFailuresAsync(NpgsqlConnection connection, PublicStudioStore store)
    {
        var wrongRoleInvite = Guid.Parse("f217f000-0000-4000-8000-000000000204");
        await InsertInvitationAsync(connection, wrongRoleInvite, "runner-role@example.invalid", accepted: true);
        var wrongRole = store.CreatePendingInvitedAdmin("Synthetic", "Role", "runner-role@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", wrongRoleInvite);
        await ExecAsync(connection, "UPDATE public.admin_users SET role='SuperAdmin' WHERE app_id=@id", ("id", wrongRole.Id));
        await ExpectStoreFailureAsync(connection, store, wrongRole.Id, wrongRoleInvite, wrongRole.Email,
            SupabaseProvisioningResult.Confirmed(Guid.NewGuid()), "Wrong role is rejected.");

        var unconsumedInvite = Guid.Parse("f217f000-0000-4000-8000-000000000205");
        await InsertInvitationAsync(connection, unconsumedInvite, "runner-unconsumed@example.invalid", accepted: false);
        var unconsumed = store.CreatePendingInvitedAdmin("Synthetic", "Unconsumed", "runner-unconsumed@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", unconsumedInvite);
        await ExpectStoreFailureAsync(connection, store, unconsumed.Id, unconsumedInvite, unconsumed.Email,
            SupabaseProvisioningResult.Confirmed(Guid.NewGuid()), "Unconsumed invitation is rejected.");

        var emailInvite = Guid.Parse("f217f000-0000-4000-8000-000000000206");
        await InsertInvitationAsync(connection, emailInvite, "runner-email@example.invalid", accepted: true);
        var mismatched = store.CreatePendingInvitedAdmin("Synthetic", "Email", "runner-email@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", emailInvite);
        await ExpectStoreFailureAsync(connection, store, mismatched.Id, emailInvite, "different@example.invalid",
            SupabaseProvisioningResult.Confirmed(Guid.NewGuid()), "Mismatched supplied email is rejected.");

        var wrongInviteId = Guid.Parse("f217f000-0000-4000-8000-000000000207");
        await InsertInvitationAsync(connection, wrongInviteId, "runner-wrong-invite@example.invalid", accepted: true);
        var wrongInvitation = store.CreatePendingInvitedAdmin("Synthetic", "WrongInvite", "runner-wrong-invite@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", wrongInviteId);
        await ExpectStoreFailureAsync(connection, store, wrongInvitation.Id, Guid.Parse("f217f000-0000-4000-8000-000000000208"),
            wrongInvitation.Email, SupabaseProvisioningResult.Confirmed(Guid.NewGuid()), "Incorrect invitation/account correlation is rejected.");
    }

    private static async Task RunRollbackCaseAsync(NpgsqlConnection connection, PublicStudioStore store)
    {
        var invite = Guid.Parse("f217f000-0000-4000-8000-000000000209");
        await InsertInvitationAsync(connection, invite, "runner-rollback@example.invalid", accepted: true);
        var admin = store.CreatePendingInvitedAdmin("Synthetic", "Rollback", "runner-rollback@example.invalid",
            "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", invite);
        await ExecAsync(connection, "CREATE TABLE public.fixture_fail_activation (app_id uuid PRIMARY KEY, enabled boolean NOT NULL)");
        await ExecAsync(connection, "INSERT INTO public.fixture_fail_activation VALUES (@id,true)", ("id", admin.Id));
        await ExecAsync(connection, """
            CREATE FUNCTION public.fixture_reject_activation() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.is_active AND EXISTS (SELECT 1 FROM public.fixture_fail_activation
                    WHERE app_id=NEW.app_id AND enabled) THEN
                RAISE EXCEPTION 'synthetic activation failure';
              END IF;
              RETURN NEW;
            END $$
            """);
        await ExecAsync(connection, "CREATE TRIGGER fixture_reject_activation BEFORE UPDATE OF is_active ON public.admin_users FOR EACH ROW EXECUTE FUNCTION public.fixture_reject_activation()");
        var before = await ReadAccountAsync(connection, admin.Id);
        await ExpectStoreFailureAsync(connection, store, admin.Id, invite, admin.Email,
            SupabaseProvisioningResult.Confirmed(Guid.NewGuid()), "Second-write failure triggers rollback.");
        var after = await ReadAccountAsync(connection, admin.Id);
        Require(after == before && after.State == "Pending" && !after.Active && after.Subject is null,
            "Rollback preserves the complete pending identity and inactive account snapshot.");
    }

    private static async Task RunConcurrentCaseAsync(NpgsqlConnection connection)
    {
        var inviteA = Guid.Parse("f217f000-0000-4000-8000-000000000212");
        var inviteB = Guid.Parse("f217f000-0000-4000-8000-000000000213");
        const string emailA = "runner-race-a@example.invalid";
        const string emailB = "runner-race-b@example.invalid";
        await InsertInvitationAsync(connection, inviteA, emailA, accepted: true);
        await InsertInvitationAsync(connection, inviteB, emailB, accepted: true);
        var provider = Guid.Parse("f217f000-0000-4000-8000-000000000214");
        using var ready = new CountdownEvent(2);
        using var start = new ManualResetEventSlim(false);
        var cs = connection.ConnectionString;
        var first = Task.Run(() => ConcurrentStoreAttemptAsync(cs, emailA, inviteA, provider, ready, start));
        var second = Task.Run(() => ConcurrentStoreAttemptAsync(cs, emailB, inviteB, provider, ready, start));
        if (!ready.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Synthetic concurrent sessions did not reach the barrier.");
        start.Set();
        var attempts = await Task.WhenAll(first, second);
        Require(attempts.Count(x => x.Confirmed) == 1, "Separate store sessions racing for one provider UUID produce exactly one winner.");
        var finalA = await ReadAccountAsync(connection, attempts[0].AccountId);
        var finalB = await ReadAccountAsync(connection, attempts[1].AccountId);
        var confirmedRows = new[] { finalA, finalB }.Where(x => x.State == "Confirmed").ToList();
        var pendingRows = new[] { finalA, finalB }.Where(x => x.State == "Pending").ToList();
        Require(confirmedRows.Count == 1 && pendingRows.Count == 1
            && confirmedRows[0].Active && confirmedRows[0].Subject == provider
            && !pendingRows[0].Active && pendingRows[0].Subject is null,
            "Both provider-race participants end in complete winner/unchanged-loser states.");

        // Both attempts now target the same consumed invitation and pending
        // account but carry different provider UUIDs. Invitation row locking
        // must serialize them; one succeeds and the conflicting repeat fails.
        var sharedInvite = Guid.Parse("f217f000-0000-4000-8000-000000000218");
        const string sharedEmail = "runner-race-invitation@example.invalid";
        await InsertInvitationAsync(connection, sharedInvite, sharedEmail, accepted: true);
        using (var setupContext = CreateContext(connection))
        {
            var setupStore = CreateStore(setupContext);
            _ = setupStore.CreatePendingInvitedAdmin("Synthetic", "InviteRace", sharedEmail,
                "Synthetic-only-Password-2717!", 35, "Others", "Synthetic test address", sharedInvite);
        }
        var target = await ReadAccountByInvitationAsync(connection, sharedInvite);
        using var sameInviteReady = new CountdownEvent(2);
        using var sameInviteStart = new ManualResetEventSlim(false);
        var providerA = Guid.Parse("f217f000-0000-4000-8000-000000000219");
        var providerB = Guid.Parse("f217f000-0000-4000-8000-000000000220");
        var sameInviteA = Task.Run(() => ConcurrentConfirmationAttemptAsync(cs, target.AccountId, sharedInvite,
            sharedEmail, providerA, sameInviteReady, sameInviteStart));
        var sameInviteB = Task.Run(() => ConcurrentConfirmationAttemptAsync(cs, target.AccountId, sharedInvite,
            sharedEmail, providerB, sameInviteReady, sameInviteStart));
        if (!sameInviteReady.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Same-invitation sessions did not reach the barrier.");
        sameInviteStart.Set();
        var sameInvitationResults = await Task.WhenAll(sameInviteA, sameInviteB);
        var finalSameInvite = await ReadAccountAsync(connection, target.AccountId);
        Require(sameInvitationResults.Count(x => x) == 1 && finalSameInvite.State == "Confirmed"
            && finalSameInvite.Active && (finalSameInvite.Subject == providerA || finalSameInvite.Subject == providerB),
            "Concurrent conflicting confirmations of one invitation yield one complete confirmed association.");
    }

    private static async Task<ConcurrentOutcome> ConcurrentStoreAttemptAsync(string connectionString, string email, Guid invitation,
        Guid provider, CountdownEvent ready, ManualResetEventSlim start)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var context = CreateContext(connection);
        var store = CreateStore(context);
        var user = store.CreatePendingInvitedAdmin("Synthetic", "Race", email, "Synthetic-only-Password-2717!",
            35, "Others", "Synthetic test address", invitation);
        ready.Signal();
        if (!start.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Concurrent confirmation barrier timed out.");
        try
        {
            store.ConfirmAdminProvisioning(user.Id, invitation, email, SupabaseProvisioningResult.Confirmed(provider));
            return new(user.Id, true);
        }
        catch (InvalidOperationException) { return new(user.Id, false); }
    }

    private static async Task<bool> ConcurrentConfirmationAttemptAsync(string connectionString, Guid account, Guid invitation,
        string email, Guid provider, CountdownEvent ready, ManualResetEventSlim start)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        using var context = CreateContext(connection);
        var store = CreateStore(context);
        ready.Signal();
        if (!start.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Same-invitation confirmation barrier timed out.");
        try
        {
            store.ConfirmAdminProvisioning(account, invitation, email, SupabaseProvisioningResult.Confirmed(provider));
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<AccountSnapshot> ReadAccountByInvitationAsync(NpgsqlConnection connection, Guid invitation)
    {
        await using var command = new NpgsqlCommand("SELECT id FROM public.account_identities WHERE admin_invitation_id=@invitation", connection);
        command.Parameters.AddWithValue("invitation", invitation);
        var accountId = (Guid?)await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Synthetic invitation has no pending account identity.");
        return await ReadAccountAsync(connection, accountId);
    }

    private static async Task InsertInvitationAsync(NpgsqlConnection connection, Guid id, string email, bool accepted)
    {
        const string sql = "INSERT INTO public.admin_invitations(id,email,token_hash,expires_at,accepted_at) VALUES(@id,@email,@token,now()+interval '1 day',CASE WHEN @accepted THEN now() ELSE NULL END)";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("token", "synthetic-token-hash-" + id.ToString("N"));
        command.Parameters.AddWithValue("accepted", accepted);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<AccountSnapshot> ReadAccountAsync(NpgsqlConnection connection, Guid id)
    {
        const string sql = """
            SELECT i.id,i.admin_id,u.app_id,i.login,i.admin_invitation_id,u.email,u.password_hash,u.role,u.is_active,
                   i.admin_provisioning_state,i.auth_state,i.supabase_user_id,i.updated_at
            FROM public.account_identities i JOIN public.admin_users u ON u.id=i.admin_id WHERE i.id=@id
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Synthetic account snapshot missing.");
        return new(reader.GetGuid(0), reader.GetInt64(1), reader.GetGuid(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4), reader.GetString(5), reader.GetString(6),
            reader.GetString(7), reader.GetBoolean(8), reader.GetString(9), reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11), reader.GetDateTime(12));
    }

    private static async Task ExecAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExpectConstraintFailureWithoutMutationAsync(NpgsqlConnection connection, Guid account,
        string sql, string message)
    {
        var before = await ReadAccountAsync(connection, account);
        var rejected = false;
        try { await ExecAsync(connection, sql, ("id", account)); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.CheckViolation)
        {
            rejected = true;
        }
        Require(rejected && await ReadAccountAsync(connection, account) == before, message + " State remains unchanged.");
    }

    private static async Task ExpectStoreFailureAsync(NpgsqlConnection connection, PublicStudioStore store, Guid account, Guid invitation,
        string email, SupabaseProvisioningResult result, string message)
    {
        var before = await ReadAccountAsync(connection, account);
        try { store.ConfirmAdminProvisioning(account, invitation, email, result); }
        catch (InvalidOperationException)
        {
            var after = await ReadAccountAsync(connection, account);
            Require(after == before, message + " Complete account/identity state remains unchanged.");
            return;
        }
        throw new InvalidOperationException("Expected confirmation rejection: " + message);
    }

    private static PublicMakeupDbContext CreateContext(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<PublicMakeupDbContext>().UseNpgsql(connection).Options);

    private static PublicStudioStore CreateStore(PublicMakeupDbContext context) =>
        new(context, new HttpContextAccessor(), new HarnessEnvironment(), new ConfigurationBuilder().Build(), new NullFileStore());

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "MakeupSystem.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS (runtime, approved isolated DB only): " + message);
    }

    private sealed record AccountSnapshot(Guid AccountId, long AdminId, Guid AppId, string Login, Guid? InvitationId,
        string Email, string PasswordHash, string Role, bool Active, string State, string AuthState, Guid? Subject,
        DateTime IdentityUpdatedAt);

    private sealed record HistoricalAccountSnapshot(Guid AccountId, Guid AppId, string PasswordHash, string Role,
        bool Active, string Email, long? CustomerId, long? AdminId, string Login);

    private sealed record ConcurrentOutcome(Guid AccountId, bool Confirmed);

    private sealed class NullFileStore : IAppFileStore
    {
        public void Save(string key, byte[] bytes) => throw new NotSupportedException();
        public (byte[] Bytes, string ContentType)? Read(string? key) => null;
        public void Delete(string? key) => throw new NotSupportedException();
    }

    private sealed class HarnessEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AdminProvisioningPostgresRunner";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Harness";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
