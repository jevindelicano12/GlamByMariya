using System.Data;
using Npgsql;

namespace GlamByMariya.Web.Security;

// Fixture-only adapter. Production instances fail closed; no MVC endpoint registers it.
internal sealed class CustomerProofCorrectionCandidate
{
    private readonly NpgsqlDataSource source;
    private readonly bool isolatedTest;
    internal CustomerProofCorrectionCandidate(NpgsqlDataSource source) : this(source, false) { }
    private CustomerProofCorrectionCandidate(NpgsqlDataSource source, bool isolatedTest)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.isolatedTest = isolatedTest;
    }
    internal static CustomerProofCorrectionCandidate ForIsolatedTests(NpgsqlDataSource source) => new(source, true);

    internal async Task<bool> CorrectAsync(TrustedRequestSession? session, HttpContext? context,
        long reservationId, long? expectedRevision, string? reference, Guid assetId, CancellationToken ct = default)
    {
        if (!isolatedTest || context == null || session == null ||
            !session.TryGetActor(context, out var actor) || actor == null || actor.Role != "Customer" ||
            reservationId <= 0 || expectedRevision is null or < 0 or long.MaxValue ||
            string.IsNullOrWhiteSpace(reference) || reference.Trim().Length > 150 || assetId == Guid.Empty)
            return false;
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using var command = new NpgsqlCommand("""
                SELECT v2_authz_api.fixture_correct_customer_proof(
                    @account,@subject,@role,@epoch,@generation,@reservation,@revision,@reference,@asset)
                """, connection, transaction);
            command.Parameters.AddWithValue("account", actor.AccountId);
            command.Parameters.AddWithValue("subject", actor.ProviderSubject);
            command.Parameters.AddWithValue("role", actor.Role);
            command.Parameters.AddWithValue("epoch", actor.CredentialEpoch);
            command.Parameters.AddWithValue("generation", actor.SecurityGeneration);
            command.Parameters.AddWithValue("reservation", reservationId);
            command.Parameters.AddWithValue("revision", expectedRevision.Value);
            command.Parameters.AddWithValue("reference", reference.Trim());
            command.Parameters.AddWithValue("asset", assetId);
            if (await command.ExecuteScalarAsync(ct) is not true ||
                !session.TryGetActor(context, out var current) || current != actor || ct.IsCancellationRequested)
                return false;
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception) { return false; }
    }
}
