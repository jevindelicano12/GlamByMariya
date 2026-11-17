using GlamByMariya.Core.Entities;
using GlamByMariya.Infrastructure.Services;

namespace GlamByMariya.Infrastructure.Data;

// Request-scoped read models in PostgreSQL mode; singleton lists only in Demo mode.
// Database writes are explicit commands, never inferred from edits to these lists.
public interface IStudioStore : IServiceRepository, IAddOnRepository, IBookingRepository, IPaymentRepository
{
    object Gate { get; }
    bool IsPersistent { get; }
    DateTime Now { get; }
    List<AppUser> Users { get; }
    List<Notification> Notifications { get; }
    List<AuditLog> AuditLogs { get; }
    List<AvailabilityBlock> Blocks { get; }
    BusinessSettings Settings { get; }
    AppUser AddUser(string firstName, string lastName, string email, string password, string role = "Customer", int age = 0, string gender = "", string address = "");
    // Customer registration after the email code: the password was validated and hashed before it was held.
    AppUser AddCustomerWithPasswordHash(string firstName, string lastName, string email, string passwordHash, int age, string gender, string address);
    // Elevated roles only (Admin, SuperAdmin). Never called with Customer and
    // never with browser-supplied role input; callers are bootstrap/invite flows.
    AppUser CreateElevatedUser(string firstName, string lastName, string email, string password, string role, int age, string gender, string address);
    void SaveCatalog(string kind, CatalogItem item, Guid actor);
    void DeleteCatalog(string kind, CatalogItem item, Guid actor);
    void SaveSettings(BusinessSettings settings, Guid actor);
    void AddBlock(AvailabilityBlock block, Guid actor);
    void RemoveBlock(Guid id, Guid actor);
    void RecordAudit(Guid actor, string message);
}

public interface IPersistentStudioStore : IStudioStore
{
    Booking SubmitBooking(Guid customer, Guid service, Guid[] extras, DateTime start, string address, string area,
        decimal deposit, string method, string reference, string? proof, bool agreed, bool travelAcknowledged, int people, string package,
        decimal? expectedTotal = null, decimal? expectedMinimum = null);
    void CancelBooking(Guid booking, Guid customer, bool confirmed);
    void ManageBooking(Guid booking, string action, Guid admin, string reason, decimal amount, string reference, Guid? paymentId = null, long? reviewRevision = null, string? reviewVersion = null);
    List<Payment> ReviewPayments(Guid booking);
    void RequestReschedule(Guid booking, Guid customer, DateTime start);
    void CorrectPayment(Guid booking, Guid customer, string reference, string? proof);
    string? CatalogImageKey(string kind, Guid id, string slot);
    string? PaymentQrKey(string method);
    void SaveCatalogImage(string kind, Guid id, string slot, byte[] bytes);
    void RemoveCatalogImage(string kind, Guid id, string slot);
    void SavePaymentQr(string method, byte[] bytes);
    void RemovePaymentQr(string method);
    (byte[] Bytes, string ContentType)? ReadAsset(string? key);
}

public interface ISupabaseIdentityStore
{
    bool IdentityLinkingEnabled { get; }
    bool RestrictLegacyFallback { get; }
    bool LegacyFallbackAllowed(string email, bool restrictUnknown);
    void LinkSupabaseIdentity(string email, Guid supabaseUserId);
}

// Implementations must resolve an existing subject binding, never link by email.
public interface IVerifiedProviderCustomerStore
{
    AppUser? FindVerifiedCustomer(Guid subject, string email);
    AppUser CreateVerifiedCustomer(Guid subject, EmailVerificationService.PendingRegistration pending);
}

public enum AdminProvisioningState
{
    NotApplicable,
    Pending,
    NotAttempted,
    Rejected,
    Unknown,
    Confirmed
}

public sealed record AdminProvisioningSnapshot(
    Guid AccountId, Guid InvitationId, AdminProvisioningState State, Guid? SupabaseUserId);

// Invitation-specific, fail-closed provisioning state. Historical accounts do
// not enter this state machine and retain their existing authentication behavior.
public interface IAdminProvisioningStore
{
    AppUser CreatePendingInvitedAdmin(string firstName, string lastName, string email,
        string password, int age, string gender, string address, Guid invitationId);
    void MarkAdminProvisioningOutcome(Guid accountId, AdminProvisioningState outcome);
    // invitationId must identify the already-consumed invite; the persistent
    // store verifies accepted_at and all normalized email links. The caller
    // must pass the structured provider result; an arbitrary UUID is not proof.
    void ConfirmAdminProvisioning(Guid accountId, Guid invitationId, string email, SupabaseProvisioningResult provisioningResult);
}
