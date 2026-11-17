using System.Security.Cryptography;
using GlamByMariya.Infrastructure.Integrations;
using Npgsql;

namespace GlamByMariya.Web.Security;

// Isolated preparation only: a staged object is not authorization or payment evidence.
internal sealed class PaymentProofStagingCandidate
{
    private readonly IAppFileStore files;
    private readonly bool isolatedTest;

    internal PaymentProofStagingCandidate(IAppFileStore files) : this(files, false) { }
    private PaymentProofStagingCandidate(IAppFileStore files, bool isolatedTest)
    {
        this.files = files;
        this.isolatedTest = isolatedTest;
    }
    internal static PaymentProofStagingCandidate ForIsolatedTests(IAppFileStore files) => new(files, true);

    internal enum BeginResult { Accepted, Denied, Unknown }

    // The server retains operationId across retries, including an unknown commit.
    internal async Task<BeginResult> BeginDurableAsync(TrustedRequestSession? session, HttpContext? context,
        NpgsqlDataSource source, long reservation, long revision, Guid operationId, byte[] bytes)
    {
        if (!isolatedTest || context == null || session == null || operationId == Guid.Empty ||
            !session.TryGetActor(context, out var actor) || actor == null || actor.Role != "Customer")
            return BeginResult.Denied;
        NormalizedPaymentProof image;
        try { image = PaymentProofImageValidator.Normalize(bytes); }
        catch (InvalidOperationException) { return BeginResult.Denied; }
        try
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT v2_authz_api.fixture_begin_proof_stage(
                    @account,@subject,@role,@epoch,@generation,@reservation,@revision,@id,@digest,@extension)
                """, connection, tx);
            command.Parameters.AddWithValue("account", actor.AccountId);
            command.Parameters.AddWithValue("subject", actor.ProviderSubject);
            command.Parameters.AddWithValue("role", actor.Role);
            command.Parameters.AddWithValue("epoch", actor.CredentialEpoch);
            command.Parameters.AddWithValue("generation", actor.SecurityGeneration);
            command.Parameters.AddWithValue("reservation", reservation);
            command.Parameters.AddWithValue("revision", revision);
            command.Parameters.AddWithValue("id", operationId);
            command.Parameters.AddWithValue("digest", SHA256.HashData(image.Bytes));
            command.Parameters.AddWithValue("extension", image.Extension);
            if (await command.ExecuteScalarAsync() is not Guid id || id != operationId ||
                !session.TryGetActor(context, out var current) || current != actor)
                return BeginResult.Denied;
            await tx.CommitAsync();
            return BeginResult.Accepted;
        }
        catch (Exception) { return BeginResult.Unknown; }
    }

    // No caller-supplied key or owner. This result cannot be used to promote an asset.
    internal string? Stage(TrustedRequestSession? session, HttpContext? context, byte[] bytes)
    {
        if (!isolatedTest || context == null || session == null ||
            !session.TryGetActor(context, out var actor) || actor == null || actor.Role != "Customer")
            return null;

        NormalizedPaymentProof image;
        try { image = PaymentProofImageValidator.Normalize(bytes); }
        catch (InvalidOperationException) { return null; }

        var key = "ProofStaging/" + Guid.NewGuid().ToString("N") + image.Extension;
        var accepted = false;
        try
        {
            files.Save(key, image.Bytes);
            var stored = files.Read(key);
            if (stored == null || stored.Value.ContentType != image.ContentType ||
                !CryptographicOperations.FixedTimeEquals(stored.Value.Bytes, image.Bytes) ||
                !session.TryGetActor(context, out var current) || current != actor)
                return null;
            accepted = true;
            return key;
        }
        catch (Exception) { return null; }
        finally
        {
            // Delete can fail silently in the existing cloud adapter; no cleanup is claimed.
            if (!accepted) { try { files.Delete(key); } catch (Exception) { } }
        }
    }
}
