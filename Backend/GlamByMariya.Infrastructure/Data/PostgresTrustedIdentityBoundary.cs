using System.Data;
using System.Security.Claims;
using GlamByMariya.Infrastructure.Services;
using Npgsql;

namespace GlamByMariya.Infrastructure.Data;

/// <summary>
/// Inactive V2 candidate adapter. The server must pass an authenticated ASP.NET
/// protected principal. Public instances are hard-gated. The internal fixture
/// entry point is for isolated tests only, not a security boundary or runtime API.
/// </summary>
public sealed class PostgresTrustedIdentityBoundary
{
    private readonly NpgsqlDataSource source;
    private readonly bool isolatedTest;

    public PostgresTrustedIdentityBoundary(NpgsqlDataSource source) : this(source, false) { }
    private PostgresTrustedIdentityBoundary(NpgsqlDataSource source, bool isolatedTest)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.isolatedTest = isolatedTest;
    }
    internal static PostgresTrustedIdentityBoundary ForIsolatedTests(NpgsqlDataSource source) => new(source, true);

    public async Task<bool> ValidateProtectedSessionAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (!isolatedTest) return false;
        if (!TrustedActorSession.TryReadProtectedPrincipal(principal, out var session) || session == null) return false;
        return await ExecuteBooleanAsync("v2_authz_api.validate_session", session, null, ct);
    }

    public async Task<bool> UpdateCustomerFullNameAsync(
        ClaimsPrincipal? principal, string? fullName, CancellationToken ct = default)
    {
        if (!isolatedTest) return false;
        if (!TrustedActorSession.TryReadProtectedPrincipal(principal, out var session) || session == null
            || session.Role != "Customer" || string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length > 150)
            return false;
        return await ExecuteBooleanAsync("v2_authz_api.update_customer_full_name", session, fullName.Trim(), ct);
    }

    public async Task<long?> RevokeMySessionsAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (!isolatedTest) return null;
        if (!TrustedActorSession.TryReadProtectedPrincipal(principal, out var session) || session == null) return null;
        return await RevokeTrustedSessionAsync(session, ct);
    }

    // Application caller must obtain this actor from TrustedRequestSessionAccessor.
    internal async Task<long?> RevokeTrustedSessionAsync(TrustedActorSession session, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using var command = CreateCommand(connection, transaction, "v2_authz_api.revoke_my_sessions", session);
            var value = await command.ExecuteScalarAsync(ct);
            if (value is not long generation || generation <= session.SecurityGeneration)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }
            await transaction.CommitAsync(ct);
            return generation;
        }
        catch (Exception) { return null; }
    }

    private async Task<bool> ExecuteBooleanAsync(
        string function, TrustedActorSession session, string? fullName, CancellationToken ct)
    {
        try
        {
            await using var connection = await source.OpenConnectionAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            await using var command = CreateCommand(connection, transaction, function, session);
            if (fullName != null) command.Parameters.AddWithValue("full_name", fullName);
            var result = await command.ExecuteScalarAsync(ct);
            if (result is not true)
            {
                await transaction.RollbackAsync(ct);
                return false;
            }
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string function,
        TrustedActorSession session)
    {
        var sql = function switch
        {
            "v2_authz_api.validate_session" => "SELECT v2_authz_api.validate_session(@account_id,@provider_subject,@role,@credential_epoch,@security_generation)",
            "v2_authz_api.update_customer_full_name" => "SELECT v2_authz_api.update_customer_full_name(@account_id,@provider_subject,@role,@credential_epoch,@security_generation,@full_name)",
            "v2_authz_api.revoke_my_sessions" => "SELECT v2_authz_api.revoke_my_sessions(@account_id,@provider_subject,@role,@credential_epoch,@security_generation)",
            _ => throw new InvalidOperationException("Unlisted trusted-identity operation.")
        };
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("account_id", session.AccountId);
        command.Parameters.AddWithValue("provider_subject", session.ProviderSubject);
        command.Parameters.AddWithValue("role", session.Role);
        command.Parameters.AddWithValue("credential_epoch", session.CredentialEpoch);
        command.Parameters.AddWithValue("security_generation", session.SecurityGeneration);
        return command;
    }
}
