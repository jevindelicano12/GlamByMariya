using GlamByMariya.Infrastructure.Data;
using GlamByMariya.Core.Entities;

namespace GlamByMariya.Infrastructure.Services;

/// <summary>
/// Standalone orchestration for a future Admin invitation flow. It is not
/// registered in production DI or used by the normal controller path. Isolated
/// tests may call it directly or inject it through the controller test seam.
/// </summary>
public sealed class AdminInvitationProvisioningWorkflow
{
    private readonly IInvitationService invitations;
    private readonly IStudioStore store;
    private readonly IAdminProvisioningStore provisioning;
    private readonly SupabaseAuthService supabase;

    public AdminInvitationProvisioningWorkflow(
        IInvitationService invitations, IStudioStore store, SupabaseAuthService supabase)
    {
        this.invitations = invitations ?? throw new ArgumentNullException(nameof(invitations));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.supabase = supabase ?? throw new ArgumentNullException(nameof(supabase));
        provisioning = store as IAdminProvisioningStore
            ?? throw new ArgumentException("The store does not support pending Admin provisioning.", nameof(store));
    }

    public async Task<AdminInvitationWorkflowResult> AcceptAsync(
        string email, string token, string firstName, string lastName, int age,
        string gender, string address, string password, CancellationToken cancellationToken = default)
    {
        email = (email ?? "").Trim();
        gender ??= "";
        var validationError = RegistrationRules.Validate(firstName, lastName, email, age, gender, address, password);
        if (validationError != null)
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.InvalidInput);

        // Reject a pre-existing local account before irreversibly consuming the
        // invitation token. Store methods still enforce their own uniqueness.
        try
        {
            lock (store.Gate)
            {
                if (store.Users.Any(user => user.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
                    return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.InvitationUnavailable);
            }
        }
        catch (Exception)
        {
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.InvitationUnavailable);
        }

        AdminInvitation accepted;
        try
        {
            accepted = invitations.Accept(email, (token ?? "").Trim());
        }
        catch (Exception)
        {
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.InvitationUnavailable);
        }

        if (accepted == null || accepted.Id == Guid.Empty || accepted.AcceptedAt == null
            || !SameEmail(accepted.Email, email))
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.InvitationUnavailable);

        AppUser pendingAdmin;
        try
        {
            // The role is fixed here; no browser-supplied role is accepted.
            pendingAdmin = provisioning.CreatePendingInvitedAdmin(
                firstName.Trim(), lastName.Trim(), email, password, age, gender, address.Trim(), accepted.Id);
        }
        catch (Exception)
        {
            // Do not restore an already-consumed token or delete partial local
            // state. An operator may need to reconcile an interrupted attempt.
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.AccountNotEligible);
        }

        if (!IsStoredInactiveAdmin(pendingAdmin, email))
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.AccountNotEligible);

        SupabaseProvisioningResult providerResult;
        try
        {
            providerResult = await supabase.CreateConfirmedUserWithResultAsync(email, password,
                new { first_name = pendingAdmin.FirstName, last_name = pendingAdmin.LastName }, cancellationToken);
        }
        catch (Exception)
        {
            TryMarkOutcome(pendingAdmin.Id, AdminProvisioningState.Unknown);
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.ProviderNotConfirmed);
        }

        if (providerResult is null || !providerResult.IsConfirmed
            || providerResult.ProviderUserId is not { } providerUserId || providerUserId == Guid.Empty)
        {
            TryMarkOutcome(pendingAdmin.Id, StateFor(providerResult));
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.ProviderNotConfirmed);
        }

        // A confirmed remote response is not a successful workflow result. The
        // local store must link this exact invitation/account/provider tuple.
        if (cancellationToken.IsCancellationRequested)
        {
            TryMarkOutcome(pendingAdmin.Id, AdminProvisioningState.Unknown);
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.ConfirmationNotVerified);
        }

        try
        {
            provisioning.ConfirmAdminProvisioning(pendingAdmin.Id, accepted.Id, email, providerResult);
        }
        catch (Exception)
        {
            // Preserve an explicit Unknown state for reconciliation. Never retry remotely,
            // sign in, delete, or compensate for a possibly-created provider user.
            TryMarkOutcome(pendingAdmin.Id, AdminProvisioningState.Unknown);
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.ConfirmationNotVerified);
        }

        if (!IsStoredActiveAdmin(pendingAdmin, email))
        {
            TryMarkOutcome(pendingAdmin.Id, AdminProvisioningState.Unknown);
            return AdminInvitationWorkflowResult.Failed(AdminInvitationWorkflowStatus.ConfirmationNotVerified);
        }

        return AdminInvitationWorkflowResult.Completed;
    }

    private bool IsStoredInactiveAdmin(AppUser? candidate, string email) => IsStoredAdmin(candidate, email, active: false);

    private bool IsStoredActiveAdmin(AppUser? candidate, string email) => IsStoredAdmin(candidate, email, active: true);

    private bool IsStoredAdmin(AppUser? candidate, string email, bool active)
    {
        if (candidate == null) return false;
        try
        {
            lock (store.Gate)
            {
                var matches = store.Users.Where(user => user.Id == candidate.Id).Take(2).ToArray();
                return matches.Length == 1 && matches[0].Role == Roles.Admin
                    && matches[0].IsActive == active && SameEmail(matches[0].Email, email)
                    && SameEmail(candidate.Email, email) && candidate.Role == Roles.Admin
                    && candidate.IsActive == active;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void TryMarkOutcome(Guid accountId, AdminProvisioningState state)
    {
        try { provisioning.MarkAdminProvisioningOutcome(accountId, state); }
        catch (Exception) { /* Keep the result generic; leave reconciliation to an operator. */ }
    }

    private static AdminProvisioningState StateFor(SupabaseProvisioningResult? result) => result switch
    {
        { Outcome: SupabaseProvisioningOutcome.NotAttempted } => AdminProvisioningState.NotAttempted,
        { Outcome: SupabaseProvisioningOutcome.Unconfirmed, Failure: SupabaseProvisioningFailure.HttpRejected } => AdminProvisioningState.Rejected,
        _ => AdminProvisioningState.Unknown
    };

    private static bool SameEmail(string? left, string right) => left != null
        && string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}

public enum AdminInvitationWorkflowStatus
{
    Completed,
    InvalidInput,
    InvitationUnavailable,
    AccountNotEligible,
    ProviderNotConfirmed,
    ConfirmationNotVerified
}

/// <summary>Contains only a generic status; never carries tokens, passwords or provider response data.</summary>
public sealed record AdminInvitationWorkflowResult(bool Succeeded, AdminInvitationWorkflowStatus Status)
{
    public static AdminInvitationWorkflowResult Completed { get; } = new(true, AdminInvitationWorkflowStatus.Completed);

    public static AdminInvitationWorkflowResult Failed(AdminInvitationWorkflowStatus status) =>
        new(false, status == AdminInvitationWorkflowStatus.Completed
            ? AdminInvitationWorkflowStatus.ConfirmationNotVerified : status);
}
